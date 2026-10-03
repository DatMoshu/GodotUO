"""Rotate and mirror a multi, remapping the pieces whose art faces a direction.

Positions. A wall piece does not fill its cell: an EW piece draws the south edge of its cell (the line
y + 1, from x to x + 1), an NS piece the east edge (the line x + 1), a post stands on a vertex. So a
turned or mirrored building cannot move cells alone: a wall that drew a cell's east edge draws, once
mirrored, a west edge, which is the east edge of the next cell. The transform therefore works on edges
and vertices of the grid, and puts each piece in the cell whose edge it lands on. A corner piece that
draws both faces (the front corner the originals use) becomes its two straight runs.

Ids. The remap table is built from a mined catalogue (the families a piece belongs to, by neighbour
signature, roof side, stair ascent), from a style set (its roles), and from tiledata names ("rug east").
Door ids follow ModernUO's door layout: 16 ids from a base, closed/open pairs for 8 facings.

  build_table(...)            -> table (JSON: guo.multi.orient/1)
  table_from_styles(styles)   -> table
  transform(comps, op, table) -> comps; op is rot90 (clockwise seen from above), rot180, rot270,
                                 mirror_x (east becomes west) or mirror_y (north becomes south)
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from generate import faces  # noqa: E402
from multifile import Component  # noqa: E402

SCHEMA = "guo.multi.orient/1"
OPS = ("rot90", "rot180", "rot270", "mirror_x", "mirror_y")

# a letter's image under each base transform (x east, y south; rot90 turns clockwise seen from above)
LETTER = {
    "rot90": {"N": "E", "E": "S", "S": "W", "W": "N"},
    "mirror_x": {"N": "N", "E": "W", "S": "S", "W": "E"},
    "mirror_y": {"N": "S", "E": "E", "S": "N", "W": "W"},
}
# ModernUO DoorFacing order: WestCW EastCCW WestCCW EastCW (a wall along x), SouthCW NorthCCW SouthCCW NorthCW
DOOR_PERM = {
    "rot90": {0: 7, 1: 6, 2: 5, 3: 4, 4: 0, 5: 1, 6: 2, 7: 3},
    "mirror_x": {0: 1, 1: 0, 2: 3, 3: 2, 4: 6, 5: 7, 6: 4, 7: 5},
    "mirror_y": {0: 2, 1: 3, 2: 0, 3: 1, 4: 5, 5: 4, 6: 7, 7: 6},
}
SIDE_WORDS = {"north": "N", "east": "E", "south": "S", "west": "W"}
WORD_OF = {v: k for k, v in SIDE_WORDS.items()}


def base_ops(op: str) -> list[str]:
    return {"rot90": ["rot90"], "rot180": ["rot90", "rot90"], "rot270": ["rot90"] * 3,
            "mirror_x": ["mirror_x"], "mirror_y": ["mirror_y"]}[op]


def map_letters(s: str, base: str) -> str:
    """A side key ('N', 'NE', 'ridge_x', 'N/EW') under a base transform."""
    if s in ("ridge_x", "ridge_y"):
        return {"ridge_x": "ridge_y", "ridge_y": "ridge_x"}[s] if base == "rot90" else s
    return "".join(LETTER[base].get(ch, ch) for ch in s)


def sorted_sig(s: str) -> str:
    return "".join(ch for ch in "NESW" if ch in s) or "-"


def map_key(key: str, base: str) -> str:
    """A catalogue key: a roof side, a signature, or a stair 'ascent/signature'."""
    if "/" in key:
        a, s = key.split("/", 1)
        return f"{sorted_sig(map_letters(a, base)) if a != '-' else '-'}/" \
               f"{sorted_sig(map_letters(s, base)) if s != '-' else '-'}"
    if key in ("-", "cap", "NSEW", "NS", "EW") or key.startswith("ridge"):
        if key in ("NS", "EW") and base == "rot90":
            return {"NS": "EW", "EW": "NS"}[key]
        return map_letters(key, base) if key.startswith("ridge") else key
    return sorted_sig(map_letters(key, base)) if re.fullmatch(r"[NESW]+", key) else key


def hexs(i: int) -> str:
    return f"{i:#06x}"


def door_base(i: int, bases: list[int]) -> int | None:
    for b in bases:
        if b <= i < b + 16:
            return b
    return None


def build_table(families: dict, pieces: dict | None = None, names: dict[int, str] | None = None,
                door_bases: list[int] | None = None) -> dict:
    """The remap table from a mined catalogue (families.json, pieces.json) and optionally tiledata names."""
    pieces = pieces or {}
    inn = lambda i: set(pieces.get(i, {}).get("in", []))
    wall: dict = {}
    maps: dict = {"rot90": {}, "mirror_x": {}, "mirror_y": {}}
    bases = list(door_bases or [])
    for mat, fam in families.items():
        for role in ("wall", "window", "post"):
            for h, sigs in fam.get(role, {}).items():
                def best(sigkey, like):
                    c = sigs.get(sigkey) or []
                    return max(c, key=lambda i: len(inn(i) & like)) if c else None
                # the commonest signature of each id
                seen: dict = {}
                for sig, ids in sigs.items():
                    for i in ids:
                        seen.setdefault(i, []).append(sig)
                for i, sg in seen.items():
                    counts = pieces.get(i, {}).get("signature") or {}
                    sig = max(counts, key=counts.get) if counts else sg[0]
                    ew, ns = faces(sig if sig != "-" else "")
                    axis = "EWNS" if ew and ns else "EW" if ew else "NS" if ns else "post"
                    like = inn(i)
                    wall[i] = {"axis": axis, "ew": i if axis == "EW" else best("EW", like) or i,
                               "ns": i if axis == "NS" else best("NS", like) or i}
        for role in ("roof", "stair"):
            for key, ids in fam.get(role, {}).items():
                for base in maps:
                    tgt = map_key(key, base)
                    cands = fam[role].get(tgt)
                    if not cands:
                        continue
                    for i in ids:
                        maps[base].setdefault(i, max(cands, key=lambda c: len(inn(c) & inn(i))))
        for d in fam.get("door", {}).get("any", []):
            b = int(d, 16) - ((int(d, 16) - 5) % 16)
            if b not in bases and 0x600 <= b < 0x900:
                bases.append(b)
    table = {"format": SCHEMA, "wall": wall, "map": maps, "door_bases": sorted(bases)}
    if names:
        add_name_maps(table, names)
    return table


def add_name_maps(table: dict, names: dict[int, str]) -> None:
    """Pieces with a side word in their tiledata name ('rug east') mapped to the piece named for the
    turned side, where exactly one tile has that name."""
    by_name: dict = {}
    for i, n in names.items():
        by_name.setdefault(n.strip().lower(), []).append(i)
    for i, n in names.items():
        words = n.strip().lower().split()
        if not any(w in SIDE_WORDS for w in words):
            continue
        for base in LETTER:
            new = [WORD_OF[LETTER[base][SIDE_WORDS[w]]] if w in SIDE_WORDS else w for w in words]
            hit = by_name.get(" ".join(new))
            if hit and len(hit) == 1 and hit[0] != i:
                table["map"][base].setdefault(hexs(i), hexs(hit[0]))


def table_from_styles(styles: dict) -> dict:
    """The remap table of a style set: every straight, corner, post and window by role, roof and stair
    kits by side, doors by their listed ids."""
    from styles import ids_of
    wall: dict = {}
    maps: dict = {"rot90": {}, "mirror_x": {}, "mirror_y": {}}
    bases: list = []
    for st in styles.values():
        for rs in ("walls", "foundation", "parapet", "trim", "gable_fill"):
            s = st.get(rs)
            if not s:
                continue
            ew, ns = (ids_of(s.get("EW")) or [0])[0], (ids_of(s.get("NS")) or [0])[0]
            if not (ew and ns):
                continue
            for i in ids_of(s.get("EW")):
                wall[hexs(i)] = {"axis": "EW", "ew": hexs(i), "ns": hexs(ns)}
            for i in ids_of(s.get("NS")):
                wall[hexs(i)] = {"axis": "NS", "ew": hexs(ew), "ns": hexs(i)}
            post = ids_of(s.get("post"))
            for i in post:
                wall[hexs(i)] = {"axis": "post", "ew": hexs(ew), "ns": hexs(ns)}
            for corner, v in (s.get("corners") or {}).items():
                for i in ids_of(v):
                    axis = {"SE": "EWNS", "NE": "EW", "SW": "NS", "NW": "post"}[corner]
                    wall[hexs(i)] = {"axis": axis, "ew": hexs(ew), "ns": hexs(ns)}
            for axis, v in (s.get("junctions") or {}).items():
                for i in ids_of(v):
                    wall[hexs(i)] = {"axis": axis, "ew": hexs(ew), "ns": hexs(ns)}
        for w in st.get("windows") or []:
            e, n = ids_of(w.get("EW")), ids_of(w.get("NS"))
            if e and n:
                wall[hexs(e[0])] = {"axis": "EW", "ew": hexs(e[0]), "ns": hexs(n[0])}
                wall[hexs(n[0])] = {"axis": "NS", "ew": hexs(e[0]), "ns": hexs(n[0])}
        r = st.get("roof") or {}
        for kit in ("gable", "hip"):
            k = r.get(kit) or {}
            for base in maps:
                for side, v in k.items():
                    tgt = k.get(map_key(side, base))
                    if tgt and ids_of(v) and ids_of(tgt):
                        maps[base][hexs(ids_of(v)[0])] = hexs(ids_of(tgt)[0])
        sn = (st.get("stairs") or {}).get("straight") or {}
        for base in maps:
            for asc, v in sn.items():
                tgt = sn.get(map_letters(asc, base))
                if tgt and ids_of(v) and ids_of(tgt):
                    maps[base][hexs(ids_of(v)[0])] = hexs(ids_of(tgt)[0])
        for d in (st.get("doors") or {}).values():
            for facing in ("EW", "NS"):
                c = ids_of((d.get(facing) or {}).get("closed"))
                if c:
                    b = c[0] - (0 if facing == "EW" else 8)
                    if b not in bases:
                        bases.append(b)
    return {"format": SCHEMA, "wall": wall, "map": maps, "door_bases": sorted(bases)}


# --- the transform -----------------------------------------------------------------------------

def vertex(base: str, u: int, v: int) -> tuple[int, int]:
    if base == "rot90":
        return -v, u
    if base == "mirror_x":
        return -u, v
    return u, -v


def step(base: str, c: Component, table: dict | None) -> list[Component]:
    key = hexs(c.item)
    x, y = c.x, c.y
    wall = (table or {}).get("wall", {}).get(key)
    bases = (table or {}).get("door_bases", [])
    db = door_base(c.item, bases)
    out: list[Component] = []

    def seg_ew(item):
        p, q = vertex(base, x, y + 1), vertex(base, x + 1, y + 1)
        if p[1] == q[1]:
            return Component(item, min(p[0], q[0]), p[1] - 1, c.z, c.visible), "EW"
        return Component(item, p[0] - 1, min(p[1], q[1]), c.z, c.visible), "NS"

    def seg_ns(item):
        p, q = vertex(base, x + 1, y), vertex(base, x + 1, y + 1)
        if p[0] == q[0]:
            return Component(item, p[0] - 1, min(p[1], q[1]), c.z, c.visible), "NS"
        return Component(item, min(p[0], q[0]), p[1] - 1, c.z, c.visible), "EW"
    if db is not None:
        k = (c.item - db) // 2
        parity = (c.item - db) % 2
        axis = "EW" if k < 4 else "NS"
        new_k = DOOR_PERM[base][k]
        item = db + 2 * new_k + parity
        comp, _ = (seg_ew if axis == "EW" else seg_ns)(item)
        return [comp]
    if wall:
        axis = wall["axis"]
        if axis in ("EW", "NS"):
            comp, after = (seg_ew if axis == "EW" else seg_ns)(c.item)
            return [Component(int(wall["ew" if after == "EW" else "ns"], 16), comp.x, comp.y, comp.z, comp.visible)]
        if axis == "EWNS":
            for fn in (seg_ew, seg_ns):
                comp, after = fn(int(wall["ew" if fn is seg_ew else "ns"], 16))
                comp = Component(int(wall["ew" if after == "EW" else "ns"], 16), comp.x, comp.y, comp.z, comp.visible)
                out.append(comp)
            return out
        # a post: on the vertex (x + 1, y + 1)
        v = vertex(base, x + 1, y + 1)
        return [Component(c.item, v[0] - 1, v[1] - 1, c.z, c.visible)]
    # a cell piece (floor, roof, stair, furniture): its square
    p, q = vertex(base, x, y), vertex(base, x + 1, y + 1)
    item = c.item
    tgt = (table or {}).get("map", {}).get(base, {}).get(key)
    if tgt:
        item = int(tgt, 16)
    return [Component(item, min(p[0], q[0]), min(p[1], q[1]), c.z, c.visible)]


def transform(comps: list[Component], op: str, table: dict | None = None, recentre: bool = False) -> list[Component]:
    if op not in OPS:
        raise ValueError(f"op '{op}': {', '.join(OPS)}")
    cur = list(comps)
    for base in base_ops(op):
        nxt: list[Component] = []
        for c in cur:
            nxt += step(base, c, table)
        cur = nxt
    seen: set = set()
    out = []
    for c in cur:                                   # two straights that land on one cell with one id are one piece
        k = (c.item, c.x, c.y, c.z, c.visible)
        if k not in seen:
            seen.add(k)
            out.append(c)
    if recentre and out:
        cx = (min(c.x for c in out) + max(c.x for c in out)) // 2
        cy = (min(c.y for c in out) + max(c.y for c in out)) // 2
        out = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in out]
    return out
