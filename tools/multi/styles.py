"""Style catalogue (`guo.multi.styles/1`, docs/data_formats.md section 27) and the generators' view of it.

A style is a set of pieces keyed by role. Generators never hold item ids: they ask a style for "the
south-east corner of a wall", "a window of set 2 in a north-south wall", "a gable slope facing W".

  load_styles()       the hand-authored defaults in tools/multi/styles/, then the user's own files
                      (build/multi/styles/*.json, mined from the client, never committed)
  mine_styles(dir)    styles from a mined catalogue (run.py mine): one per wall material
  check_style()       what a style lacks, as notes (a generator degrades with a note, not a crash)
  StyleCatalogue      the interface generate.build and kit.py use (wall, floor, roof, door, ...) over styles

Wall-like roles ("walls", "foundation", "parapet", "trim", "gable_fill") are all *run sets*:
`{"height", "EW", "NS", "post", "corners": {"NW","NE","SW","SE"}, "junctions": {"EW","NS"}}`. EW is a piece
along x (it draws its cell's south edge), NS along y (its east edge). A corner key names where the corner
is on the building: NW is the back corner (the originals use a plain post there), SE the front corner
that shows both faces. A missing corner falls back to the straight run, a missing post to the corner NW
or the run: every fallback is noted.
"""
from __future__ import annotations

import json
import sys
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import generate  # noqa: E402
from generate import DescriptionError, NOTHING, Piece, faces  # noqa: E402

SCHEMA = "guo.multi.styles/1"
HERE = Path(__file__).resolve().parent
RUN_SETS = ("walls", "foundation", "parapet", "trim", "gable_fill")
DEFAULT_Z = {"floor": 7, "storey": 20, "wall": 19, "foundation": 5, "roof_step": 3, "stair_step": 5, "stair_block": 10}
CORNER_SIG = {"NW": "ES", "NE": "SW", "SW": "NE", "SE": "NW"}      # a corner's neighbours, as signature()
DOOR_FACINGS = ("WestCW", "EastCCW", "WestCCW", "EastCW", "SouthCW", "NorthCCW", "SouthCCW", "NorthCW")
DOOR_TYPES = {"wood": "DarkWoodDoor", "metal": "MetalDoor"}


def hexid(v) -> int:
    return int(v, 16) if isinstance(v, str) else int(v)


def ids_of(v) -> list[int]:
    """A piece entry is one id, a hex string, or a list of them (variants, the first preferred)."""
    if v is None or v == 0 or v == "0x0000":
        return []
    return [hexid(i) for i in (v if isinstance(v, list) else [v]) if hexid(i)]


# --- files -------------------------------------------------------------------------------------

def user_dir(build: Path | None = None) -> Path:
    if build is None:
        from guo import load_config
        build = load_config().build
    return Path(build) / "multi" / "styles"


def load_file(path: Path) -> dict:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if data.get("format") != SCHEMA:
        raise DescriptionError(f"{path}: format must be '{SCHEMA}'")
    return data["styles"]


def load_styles(extra: list[Path] | None = None, build: Path | None = None, user: bool = True) -> dict:
    """key -> style. Later files win: the committed defaults, the user's folder, then `extra`."""
    out: dict = {}
    for f in sorted((HERE / "styles").glob("*.json")):
        out.update(load_file(f))
    if user:
        try:
            ud = user_dir(build)
            for f in sorted(ud.glob("*.json")) if ud.is_dir() else []:
                out.update(load_file(f))
        except Exception:
            pass
    for f in extra or []:
        out.update(load_file(f))
    return out


# --- piece choice ------------------------------------------------------------------------------

def run_piece(pset: dict, sig: str, notes: list | None = None) -> Piece:
    """The piece of a run set at a cell whose same-set neighbours are `sig` (generate.signature)."""
    def note(msg):
        if notes is not None and msg not in notes:
            notes.append(msg)

    def first(v):
        got = ids_of(v)
        return got[0] if got else None
    ew, ns = first(pset.get("EW")), first(pset.get("NS"))
    post = first(pset.get("post"))
    corners = pset.get("corners", {})
    junc = pset.get("junctions", {})
    if len(sig) >= 3 and junc:
        axis = "NS" if (sig in ("NES", "NSW")) else "EW"
        j = first(junc.get(axis))
        if j:
            return Piece(j)
    want_ew, want_ns = faces(sig)
    corner = next((k for k, s in CORNER_SIG.items() if s == sig), None)
    cp = first(corners.get(corner)) if corner else None
    if cp:
        return Piece(cp)
    if want_ew and want_ns:
        if ew and ns:
            p = Piece(ew)
            p.extra = (ns,)
            note("a style corner is built from its two straight runs (no SE corner piece)")
            return p
        return Piece(ew or ns or NOTHING)
    if want_ew:
        return Piece(ew or ns or NOTHING)
    if want_ns:
        return Piece(ns or ew or NOTHING)
    # a post: the back corner, a free end or a lone cell
    if post:
        return Piece(post)
    if corner == "NW" and (ew or ns):
        note("no post piece: a back corner takes the straight run")
        return Piece(ew or ns)
    note("no post piece: a lone wall cell takes the straight run")
    return Piece(ew or ns or NOTHING)


def weighted(floors: list) -> "Weighted":
    ids, weights = [], []
    for f in floors:
        i, w = (f["id"], f.get("weight", 1)) if isinstance(f, dict) else (f, 1)
        ids.append(hexid(i))
        weights.append(max(1, int(w)))
    out = Weighted(ids)
    out.weights = weights
    return out


class Weighted(list):
    """Floor ids with weights; generate.scatter picks by them (a fixed hash of the cell)."""
    weights: list


class Pieces(dict):
    """`cat.pieces.get(item_hex)` for generate: roles of the ids a style holds."""


class StyleCatalogue(generate.Catalogue):
    """The generate.Catalogue interface over styles. `mat` arguments are style keys."""

    def __init__(self, styles: dict):
        self.styles = styles
        self.context = __import__("collections").Counter()
        self.last_step = {}
        self.window_index: dict = {}
        self.notes: list[str] = []
        self.fam = {}
        self.pieces = Pieces()
        for key, st in styles.items():
            for rs in RUN_SETS:
                s = st.get(rs) or {}
                role = "post" if rs == "walls" else "wall"
                for k in ("EW", "NS", "post"):
                    for i in ids_of(s.get(k)):
                        self.pieces.setdefault(f"{i:#06x}", {"role": "post" if k == "post" else "wall", "height": s.get("height", 0), "in": [1]})
                for i in [i for v in list(s.get("corners", {}).values()) + list(s.get("junctions", {}).values()) for i in ids_of(v)]:
                    self.pieces.setdefault(f"{i:#06x}", {"role": "wall", "height": s.get("height", 0), "in": [1]})
            for blk in ids_of(st.get("stairs", {}).get("block")):
                self.pieces[f"{blk:#06x}"] = {"role": "floor", "height": 10, "flags": ["surface", "bridge"], "in": [1]}

    def fresh(self) -> None:
        self.last_step.clear()

    def style(self, key: str) -> dict:
        if key not in self.styles:
            raise DescriptionError(f"style '{key}' is not known (have {sorted(self.styles)[:12]})")
        return self.styles[key]

    def z(self, key: str, name: str) -> int:
        return int(self.style(key).get("z", {}).get(name, DEFAULT_Z[name]))

    def _set(self, key: str, height: int, want: tuple = RUN_SETS) -> dict:
        st = self.style(key)
        cands = [st[r] for r in want if st.get(r)]
        if not cands:
            raise DescriptionError(f"style '{key}' has no wall pieces")
        return min(cands, key=lambda s: (abs(int(s.get("height", 0)) - height), want.index(next(r for r in want if st.get(r) is s))))

    def wall(self, mat: str, height: int, sig: str, window: bool = False) -> int:
        st = self.style(mat)
        if window:
            sets = st.get("windows") or []
            if not sets:
                self.notes.append(f"style '{mat}' has no windows: a plain wall is used")
            else:
                w = sets[self.window_index.get(mat, 0) % len(sets)]
                along_ns = bool({"N", "S"} & set(sig)) and not {"E", "W"} & set(sig)
                got = ids_of(w.get("NS" if along_ns else "EW"))
                if got:
                    return got[0]
        if height <= 3 and st.get("gable_fill"):
            pset = st["gable_fill"]
        else:
            pset = self._set(mat, height, ("walls", "parapet", "trim", "gable_fill") if height >= 12 else
                             ("parapet", "trim", "walls", "gable_fill"))
        return run_piece(pset, sig, self.notes)

    def set_window(self, mat: str, index: int) -> None:
        """Put window set `index` first (a house picks one per seed)."""
        if self.style(mat).get("windows"):
            self.window_index[mat] = index

    def course(self, mat: str, total: int, sig: str) -> list[tuple[int, int]]:
        st = self.style(mat)
        cands = [st[r] for r in ("parapet", "foundation", "trim", "walls") if st.get(r)]
        if not cands:
            raise DescriptionError(f"style '{mat}' has no foundation or parapet pieces")

        def cost(s):
            h = max(1, int(s.get("height", 5)))
            return (abs(-(-total // h) * h - total), 0 if s is st.get("foundation") and total == 5 else 1)
        pset = min(cands, key=cost)
        h = max(1, int(pset.get("height", 5)))
        n = max(1, -(-total // h))
        p = run_piece(pset, sig, self.notes)
        return [(k * h, p) for k in range(n)]

    def heights(self, mat: str, role: str) -> list[int]:
        return [int(self.style(mat)[r]["height"]) for r in RUN_SETS if self.style(mat).get(r)]

    def floor(self, mat: str, n: int = 4) -> list[int]:
        fl = self.style(mat).get("floors")
        if not fl:
            raise DescriptionError(f"style '{mat}' has no floor pieces")
        return weighted(fl)

    def step(self, mat: str, ascent: str, sig: str) -> int:
        s = self.style(mat).get("stairs", {}).get("straight", {})
        got = ids_of(s.get(ascent))
        if not got:
            raise DescriptionError(f"style '{mat}' has no stair rising {ascent}")
        return got[0]

    def block(self, stair: int) -> int:
        for st in self.styles.values():
            sn = st.get("stairs", {})
            if any(stair in ids_of(v) for v in sn.get("straight", {}).values()) and ids_of(sn.get("block")):
                return ids_of(sn["block"])[0]
        for st in self.styles.values():
            if ids_of(st.get("stairs", {}).get("block")):
                return ids_of(st["stairs"]["block"])[0]
        raise DescriptionError(f"no stair block goes with stair piece {stair:#06x}")

    def roof(self, mat: str, side: str) -> int:
        r = self.style(mat).get("roof", {})
        for kit in ("gable", "hip", "eaves"):
            got = ids_of(r.get(kit, {}).get(side))
            if got:
                return got[0]
        if side == "cap":
            got = ids_of(r.get("gable", {}).get("ridge_x")) or ids_of(r.get("gable", {}).get("ridge_y"))
            if got:
                return got[0]
        raise DescriptionError(f"style '{mat}' has no roof piece '{side}'")

    def has_roof(self, mat: str, side: str) -> bool:
        try:
            self.roof(mat, side)
            return True
        except DescriptionError:
            return False

    def door(self, mat: str, facing: str = "EW", kind: str | None = None) -> int:
        doors = self.style(mat).get("doors") or {}
        if not doors:
            raise DescriptionError(f"style '{mat}' has no doors")
        d = doors.get(kind or "wood") or next(iter(doors.values()))
        e = d.get(facing) or d.get("EW") or d.get("NS")
        return hexid(e["closed"])

    def door_pair(self, mat: str, facing: str = "EW", kind: str | None = None) -> tuple[int, int]:
        doors = self.style(mat).get("doors") or {}
        d = doors.get(kind or "wood") or next(iter(doors.values()))
        e = d.get(facing) or d.get("EW") or d.get("NS")
        return hexid(e["closed"]), hexid(e["open"])

    def door_type(self, mat: str, kind: str | None) -> str:
        doors = self.style(mat).get("doors") or {}
        d = doors.get(kind or "wood") or (next(iter(doors.values())) if doors else {})
        return d.get("type") or DOOR_TYPES.get(kind or "wood", "DarkWoodDoor")

    def post(self, mat: str, height: int) -> int:
        st = self.style(mat)
        pset = self._set(mat, height)
        got = ids_of(pset.get("post")) or ids_of(st.get("posts", {}).get("any"))
        return got[0] if got else run_piece(pset, "", self.notes)

    def ladder(self, mat: str) -> int | None:
        got = ids_of(self.style(mat).get("stairs", {}).get("ladder"))
        return got[0] if got else None


# --- checking ----------------------------------------------------------------------------------

def check_style(key: str, st: dict) -> list[str]:
    """Notes on what a style lacks, role by role (an empty list: it can build every kind)."""
    n = []
    w = st.get("walls") or {}
    if not ids_of(w.get("EW")) or not ids_of(w.get("NS")):
        n.append("walls: needs EW and NS")
    if not ids_of(w.get("post")):
        n.append("walls: no post (back corners take the straight run)")
    if not (w.get("corners") or {}).get("SE"):
        n.append("walls: no SE corner (the front corner is built from two runs)")
    if not st.get("windows"):
        n.append("windows: none")
    if not st.get("doors"):
        n.append("doors: none")
    if not st.get("floors"):
        n.append("floors: none")
    if not st.get("foundation"):
        n.append("foundation: none")
    g = (st.get("roof") or {}).get("gable") or {}
    if not all(ids_of(g.get(k)) for k in ("N", "S", "E", "W")):
        n.append("roof.gable: needs N, S, E and W")
    if not (ids_of(g.get("ridge_x")) or ids_of(g.get("ridge_y"))):
        n.append("roof.gable: no ridge (even spans only)")
    if not (st.get("roof") or {}).get("hip"):
        n.append("roof.hip: none (a hip roof uses the gable slopes)")
    if not ids_of((st.get("roof") or {}).get("flat")) and not st.get("floors"):
        n.append("roof.flat: none")
    sn = st.get("stairs") or {}
    if not sn.get("straight") or not ids_of(sn.get("block")):
        n.append("stairs: needs straight (N/E/S/W) and block")
    if not sn.get("turned"):
        n.append("stairs.turned: none (a turn uses the straight flights and a landing)")
    if not ids_of(sn.get("ladder")):
        n.append("stairs.ladder: none")
    if not st.get("z"):
        n.append("z: defaults (floor 7, storey 20)")
    if not st.get("hues"):
        n.append("hues: unrestricted")
    return n


def all_ids(st: dict) -> set[int]:
    """Every item id a style holds (to check against tiledata)."""
    out: set[int] = set()

    def walk(v, key=""):
        if isinstance(v, dict):
            for k, x in v.items():
                walk(x, k)
        elif isinstance(v, list):
            for x in v:
                walk(x, key)
        elif key in ("hues", "z", "height", "weight", "type", "name", "material", "note"):
            return
        elif isinstance(v, str) and v.startswith("0x"):
            out.add(int(v, 16))
        elif isinstance(v, int) and key not in ("hues",) and v:
            out.add(v)
    for k, v in st.items():
        if k not in ("name", "material", "hues", "z", "note"):
            walk(v, k)
    return out


def check_ids(styles: dict, data_dir: Path) -> list[str]:
    """Items a style names that tiledata does not know (a mined set read against another client)."""
    from guo.uoread import TileData
    td = TileData(data_dir)
    return [f"style '{k}': item {i:#06x} is not in tiledata" for k, st in styles.items()
            for i in sorted(all_ids(st)) if td.static(i) is None]


# --- mining ------------------------------------------------------------------------------------

def _first_in(cat, ids: list[str]) -> set:
    return set(cat._in(ids[0])) if ids else set()


def _wall_set(cat, mat: str, h: int) -> dict | None:
    def pick(sig, window=False):
        try:
            p = cat.wall(mat, h, sig, window=window)
        except (DescriptionError, KeyError):
            return None
        return None if getattr(p, "extra", ()) or p < 0 else int(p)
    s = {"height": h, "EW": pick("EW"), "NS": pick("NS")}
    if not s["EW"] or not s["NS"]:
        return None
    post = pick("")
    if post:
        s["post"] = post
    corners = {}
    for name, sig in CORNER_SIG.items():
        p = pick(sig)
        if p and p not in (s["EW"], s["NS"]) and (name != "NW" or p != post):
            corners[name] = p
    if corners:
        s["corners"] = corners
    junc = {a: p for a, sig in (("EW", "ESW"), ("NS", "NSW")) if (p := pick(sig)) and p not in (s["EW"], s["NS"])}
    if junc:
        s["junctions"] = junc
    return s


NON_ID = ("height", "weight", "hues", "z", "type", "name", "material", "note", "open_width")


def to_hex(o, key=""):
    """A style with its item ids as hex strings (the file form)."""
    if key in NON_ID:
        return o
    if isinstance(o, dict):
        return {k: to_hex(v, k) for k, v in o.items()}
    if isinstance(o, list):
        return [to_hex(v, key) for v in o]
    if isinstance(o, int) and not isinstance(o, bool) and key not in NON_ID:
        return f"{int(o):#06x}"
    return int(o) if isinstance(o, int) and not isinstance(o, bool) else o


def door_base(i: int) -> int:
    return i - ((i - 5) % 16)


def mine_styles(folder: Path, min_walls: int = 6) -> dict:
    """One style per wall material of a mined catalogue (run.py mine): the pieces the client's own
    houses use together (co-occurrence in the same multis), a roof, floor and stair set paired by
    the multis they share. Written to the user's folder only; ids are client data."""
    cat = generate.Catalogue(folder)
    fam = cat.fam
    sc_pieces = cat.pieces
    out: dict = {}
    roof_mats = [m for m, f in fam.items() if all(k in f.get("roof", {}) for k in ("N", "S", "E", "W"))]
    both = [m for m in roof_mats if all(k in fam[m]["roof"] for k in ("ridge_x", "ridge_y"))]
    roof_mats = both or roof_mats                      # a kit with both ridges builds a ridge along either axis
    stair_mats = [m for m, f in fam.items() if f.get("stair")]

    def share(a_ids: list[str], b_ids: list[str]) -> int:
        a = {m for i in a_ids[:4] for m in cat._in(i)}
        return sum(1 for i in b_ids[:4] for m in cat._in(i) if m in a)
    for mat, f in sorted(fam.items()):
        walls = f.get("wall", {})
        hs = [int(h) for h in walls if int(h) >= 15 and walls[h].get("EW") and walls[h].get("NS")]
        if not hs:
            continue
        h = max(hs, key=lambda x: (sum(len(v) for v in walls[str(x)].values()), x))
        if sum(len(v) for v in walls[str(h)].values()) < min_walls:
            continue
        cat.fresh()
        ws = _wall_set(cat, mat, h)
        if not ws:
            continue
        st = {"name": mat.title(), "material": mat, "hues": [0],
              "z": {"floor": 7, "storey": 20, "wall": h, "roof_step": 3, "stair_step": 5, "stair_block": 10},
              "walls": ws}
        anchor = walls[str(h)]["EW"]
        # windows: an EW piece with the NS piece the same multis use
        win = f.get("window", {})
        wh = next((k for k in (str(h), str(h - 1)) if k in win), None)
        if wh:
            sets = []
            for e in win[wh].get("EW", [])[:3]:
                nss = win[wh].get("NS", [])
                if nss:
                    best = max(nss, key=lambda n: len(set(cat._in(e)) & set(cat._in(n))))
                    sets.append({"EW": hexid(e), "NS": hexid(best)})
            if sets:
                st["windows"] = sets
        # foundation and parapet: the low pieces that belong with these walls
        for role, most in (("foundation", 5), ("parapet", 3)):
            try:
                lh = cat.low(mat, most)
                low = _wall_set(cat, mat, lh) if lh < h else None
            except DescriptionError:
                low = None
            if low:
                st[role] = low
        # gable fill: the 3-high pieces
        if "3" in walls and walls["3"].get("EW") and walls["3"].get("NS"):
            st["gable_fill"] = {"height": 3, "EW": hexid(walls["3"]["EW"][0]), "NS": hexid(walls["3"]["NS"][0])}
        # doors: bases found in the material's door list
        bases: dict = {}
        for d in f.get("door", {}).get("any", []):
            i = hexid(d)
            nm = (sc_pieces.get(d, {}).get("name") or "").lower()
            if 0x600 <= i < 0x700 and "door" in nm:
                bases.setdefault(door_base(i), nm)
        doors = {}
        for base, nm in bases.items():
            kind = "metal" if "metal" in nm or "iron" in nm else "wood"
            if kind in doors:
                kind = f"{kind}_{base:#x}"
            doors[kind] = {"type": DOOR_TYPES.get(kind.split("_")[0], "DarkWoodDoor"),
                           "EW": {"closed": base, "open": base + 1}, "NS": {"closed": base + 8, "open": base + 9}}
        if doors:
            st["doors"] = doors
        # floors: this material's, else the best paired by shared multis
        fl_mat = mat if f.get("floor", {}).get("NESW") else max(
            (m for m in fam if fam[m].get("floor", {}).get("NESW")), key=lambda m: share([anchor[0]], fam[m]["floor"]["NESW"]), default=None)
        if fl_mat:
            try:
                ids = cat.floor(fl_mat)
                st["floors"] = [{"id": i, "weight": 3 if k == 0 else 1} for k, i in enumerate(ids)]
            except DescriptionError:
                pass
        # roof: the roof family the same multis use
        if roof_mats:
            rm = max(roof_mats, key=lambda m: share([anchor[0]], fam[m]["roof"]["N"]))
            r = fam[rm]["roof"]
            gable = {k: hexid(r[k][0]) for k in ("N", "S", "E", "W", "ridge_x", "ridge_y") if r.get(k)}
            hip = {k: hexid(r[k][0]) for k in ("NW", "NE", "SW", "SE") if r.get(k)}
            if r.get("NSEW"):
                hip["cap"] = hexid(r["NSEW"][0])
            elif r.get("cap"):
                hip["cap"] = hexid(r["cap"][0])
            st["roof"] = {"gable": gable}
            if len(hip) >= 4:
                st["roof"]["hip"] = hip
            if st.get("floors"):
                st["roof"]["flat"] = st["floors"][0]["id"]
        # stairs: this material's, else the best paired
        sm = mat if f.get("stair") else max(stair_mats, key=lambda m: share([anchor[0]], [i for v in fam[m]["stair"].values() for i in v]), default=None)
        if sm:
            straight = {}
            for asc in "NESW":
                try:
                    straight[asc] = cat.step(sm, asc, "")
                except DescriptionError:
                    pass
            if len(straight) == 4:
                try:
                    st["stairs"] = {"straight": straight, "block": cat.block(straight["N"])}
                except DescriptionError:
                    pass
        st["note"] = "mined from the client's own multis; do not commit"
        out[mat.replace(" ", "_")] = st
    return out


def write_mined(folder: Path, out_dir: Path) -> Path:
    styles = mine_styles(folder)
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "mined.json"
    path.write_text(json.dumps({"format": SCHEMA, "provenance": "mined from the user's client; local data, never committed",
                                "styles": to_hex(styles)}, indent=1), encoding="utf-8")
    return path


def seed_hash(*parts) -> int:
    return zlib.crc32(",".join(map(str, parts)).encode())
