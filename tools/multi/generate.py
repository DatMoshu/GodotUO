"""Expand a multi description into components (docs/data_formats.md, "Multi descriptions").

Every piece is looked up in the mined catalogue (families.json, pieces.json):
walls, windows and foundations by material, height and neighbour signature;
floors by material; entrance steps and stairs by ascent and signature; roofs by
slope side. Among equal candidates the picker prefers pieces the client's own
multis use together, so a style stays one style.

Local grid: the outer walls stand on the lines x = 0, x = W, y = 0, y = H; the
floor fills x 1..W, y 1..H (as the client's houses do). The result is centred on
(W // 2, H // 2), like the originals.
"""
from __future__ import annotations

import collections
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from multifile import Component  # noqa: E402

DIRS = (("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0))
SIDE_STEP = {"S": (0, 1), "N": (0, -1), "E": (1, 0), "W": (-1, 0)}
TOWARD = {"S": "N", "N": "S", "E": "W", "W": "E"}      # the ascent of steps outside a door on that side
CENTRE_MARKER = 0x0001                                   # "nodraw", invisible, as the originals carry


class DescriptionError(ValueError):
    pass


def signature(cells, x: int, y: int) -> str:
    return "".join(d for d, dx, dy in DIRS if (x + dx, y + dy) in cells)


def scatter(ids: list[int], x: int, y: int) -> int:
    """A floor variant for a cell: the first piece on about half the cells, the others scattered
    by a hash of the cell, so large floors and roofs show no diagonal stripes."""
    if len(ids) == 1:
        return ids[0]
    h = ((x * 73856093) ^ (y * 19349663)) & 0xFFFF
    return ids[0] if h % 2 == 0 else ids[1 + (h >> 1) % (len(ids) - 1)]


def faces(sig: str) -> tuple[bool, bool]:
    """(EW, NS): the faces a wall cell with neighbours `sig` needs. A piece draws its cell's south
    edge (EW) or east edge (NS). The south edge is needed when the run goes on west, or when it
    goes east and no N-S wall crosses the cell (a run's end at a door); where one crosses, that
    wall's line is the building's edge and the south edge would stick out past it. Likewise the
    east edge. So a front corner (NW) takes both (the originals' V), NE the NS face, SW the EW
    face, and a back corner (ES) neither (the originals' post)."""
    return ("W" in sig or ("E" in sig and not {"N", "S"} & set(sig)),
            "N" in sig or ("S" in sig and not {"E", "W"} & set(sig)))


class Piece(int):
    """A wall piece, with the pieces that stand with it in its cell (`extra`): a corner the family
    has no piece for is built from its two straight faces. A negative piece is "nothing here"."""
    extra: tuple = ()


NOTHING = Piece(-1)


class Catalogue:
    def __init__(self, folder: Path):
        self.fam = json.loads((folder / "families.json").read_text(encoding="utf-8"))
        self.pieces = json.loads((folder / "pieces.json").read_text(encoding="utf-8"))
        self.context: collections.Counter = collections.Counter()
        self.last_step: dict = {}

    def fresh(self) -> None:
        """Forget what earlier builds picked: a build never depends on what was built before it."""
        self.context.clear()
        self.last_step.clear()

    def _in(self, item: str) -> list[int]:
        return self.pieces.get(item, {}).get("in", [])

    def choose(self, candidates: list[str]) -> int:
        """The candidate most used alongside what was already picked (then the most used)."""
        if not candidates:
            raise KeyError("no candidates")
        best = max(enumerate(candidates), key=lambda ic: (sum(self.context[m] for m in self._in(ic[1])), -ic[0]))[1]
        for m in self._in(best):
            self.context[m] += 1
        return int(best, 16)

    def material(self, name: str) -> dict:
        if name not in self.fam:
            raise DescriptionError(f"material '{name}' is not in the catalogue (have e.g. {sorted(self.fam)[:12]})")
        return self.fam[name]

    def low(self, mat: str, most: int = 6) -> int:
        """The height of the low pieces (a foundation, a parapet) that belong with the walls
        already picked: a material's 5-high and 3-high pieces are often different sets, and the
        client's castles top and found their stone walls with that wall's own 3-high pieces."""
        fam = self.material(mat).get("wall", {})
        hs = [int(h) for h in fam if int(h) <= most]
        if not hs:
            raise DescriptionError(f"material '{mat}' has no wall pieces {most} high or lower")

        def fit(h):
            cands = [i for ids in fam[str(h)].values() for i in ids]
            return max(sum(self.context[m] for m in self._in(c)) for c in cands)
        return max(hs, key=lambda h: (fit(h), -abs(h - 5), h))

    def set_heights(self, mat: str) -> tuple[int, ...]:
        """The wall heights of the set already in use for this material, tallest first: a
        material's pieces of other heights are often another set (another colour); all of its
        heights while nothing is picked yet."""
        fam = self.material(mat).get("wall", {})

        def fit(h):
            return max(sum(self.context[m] for m in self._in(c)) for ids in fam[h].values() for c in ids)
        fits = {int(h): fit(h) for h in fam}
        best = max(fits.values(), default=0)
        keep = [h for h, f in fits.items() if best == 0 or f * 4 >= best]
        return tuple(sorted(keep, reverse=True))

    def course(self, mat: str, total: int, sig: str) -> list[tuple[int, int]]:
        """(dz, item) low pieces stacked to at least `total` (never short: a floor sits on it)."""
        if total > 6 and not any(h <= 6 for h in self.heights(mat, "wall")):
            # a family with no low pieces (a tall parapet of full walls): one course of its walls
            return [(0, self.wall(mat, total, sig))]
        h = self.low(mat, max(3, min(total, 6)))
        n = max(1, -(-total // h))
        return [(k * h, self.wall(mat, h, sig)) for k in range(n)]

    def heights(self, mat: str, role: str) -> list[int]:
        return sorted(int(h) for h in self.material(mat).get(role, {}))

    def wall(self, mat: str, height: int, sig: str, window: bool = False) -> int:
        """A wall (or window) piece of about `height` whose neighbours match `sig`."""
        fam = self.material(mat)
        roles = ("window",) if window else ("wall", "post")
        hs = sorted({int(h) for r in roles for h in fam.get(r, {})}, key=lambda h: (abs(h - height), -h))
        if not hs:
            raise DescriptionError(f"material '{mat}' has no {'/'.join(roles)} pieces")
        for h in hs[:2]:
            got = lambda s: [i for r in roles for i in fam.get(r, {}).get(str(h), {}).get(s, [])]
            if sig and not window and not self.fits(got(sig), sig):
                joint = self.joint(fam, roles, h, sig, got)
                if joint is not None:
                    return joint
            for s in fallbacks(sig):
                cands = self.fits(got(s), sig) if (s == sig and sig and not window) else got(s)
                if cands:
                    return self.choose(cands)
        raise DescriptionError(f"material '{mat}' has no {roles[0]} piece near height {height} for '{sig or '-'}'")

    def fits(self, cands: list[str], sig: str) -> list[str]:
        """The candidates whose own commonest signature needs the same faces as `sig` (see
        `joint`): the mined sets list a straight at a back corner or a run's end where the
        originals happened to put one, and that straight draws a face past the corner."""
        need = faces
        def main(i):
            seen = self.pieces.get(i, {}).get("signature") or {}
            return max(seen, key=seen.get) if seen else None
        return [i for i in cands if main(i) is None or need(main(i)) == need(sig)]

    def joint(self, fam: dict, roles, h: int, sig: str, got) -> Piece | None:
        """A corner, join or end the family has no fitting piece for, built from the faces it
        needs (see `faces`): its V piece or both straights, one straight, or a post (or nothing)."""
        ew = self.fits(got("EW"), "EW") or got("W") or got("E")
        ns = self.fits(got("NS"), "NS") or got("N") or got("S")
        want_ew, want_ns = faces(sig)
        if want_ew and want_ns:
            v = self.fits(got("NW"), "NW")
            if v:
                return Piece(self.choose(v))
            if ew and ns:
                p = Piece(self.choose(ew))
                p.extra = (self.choose(ns),)
                return p
            return None
        if want_ew:
            return Piece(self.choose(ew)) if ew else None
        if want_ns:
            return Piece(self.choose(ns)) if ns else None
        posts = self.fits(got("ES"), "ES") or [i for ids in fam.get("post", {}).get(str(h), {}).values() for i in ids]
        return Piece(self.choose(posts)) if posts else NOTHING

    def floor(self, mat: str, n: int = 4) -> list[int]:
        fam = self.material(mat).get("floor", {})
        cands = fam.get("NESW") or next(iter(fam.values()), [])
        if not cands:
            raise DescriptionError(f"material '{mat}' has no floor pieces")
        first = self.choose(cands)
        # the interior variants the same originals use with the first
        with_first = [c for c in cands[:12] if set(self._in(c)) & set(self._in(f"{first:#06x}"))]
        return [first] + [int(c, 16) for c in with_first if int(c, 16) != first][: n - 1]

    def step(self, mat: str, ascent: str, sig: str) -> int:
        """A step piece; the one already used for this material and ascent again wherever the
        originals use it with this signature (their rows are one piece end to end)."""
        last = self.last_step.get((mat, ascent))
        if last is not None and f"{ascent}/{sig or '-'}" in (self.pieces.get(f"{last:#06x}", {}).get("step") or {}):
            return last
        fam = self.material(mat).get("stair", {})
        for s in fallbacks(sig):
            cands = fam.get(f"{ascent}/{s or '-'}")
            if cands:
                self.last_step[(mat, ascent)] = self.choose(cands)
                return self.last_step[(mat, ascent)]
        raise DescriptionError(f"material '{mat}' has no step rising {ascent} for '{sig or '-'}'")

    def roof(self, mat: str, side: str) -> int:
        fam = self.material(mat).get("roof", {})
        cands = fam.get(side)
        if not cands:
            raise DescriptionError(f"roof material '{mat}' has no '{side}' piece (has {sorted(fam)})")
        return self.choose(cands)

    def door(self, mat: str) -> int:
        cands = self.material(mat).get("door", {}).get("any") or ["0x06a5"]
        return int(cands[0], 16)

    def post(self, mat: str, height: int) -> int:
        """A free-standing post (a porch corner); a lone wall piece when the material has no posts."""
        posts = self.material(mat).get("post", {})
        hs = sorted(posts, key=lambda h: (abs(int(h) - height), -int(h)))
        for h in hs:
            cands = [i for ids in posts[h].values() for i in ids]
            if cands:
                return self.choose(cands)
        return self.wall(mat, height, "")

    def block(self, stair: int) -> int:
        """The solid block a staircase stands on: the 10-high bridge piece the same originals
        stack under this stair piece (0x0738 under wooden stairs, 0x0750 under stone ones)."""
        mine = set(self._in(f"{stair:#06x}"))
        best, score = None, 0
        for item, p in self.pieces.items():
            if p.get("height") == 10 and p.get("role") != "stair" and {"surface", "bridge"} <= set(p.get("flags", [])):
                n = len(mine & set(p.get("in", [])))
                if n > score:
                    best, score = item, n
        if best is None:
            raise DescriptionError(f"no stair block goes with stair piece {stair:#06x}")
        return int(best, 16)


def fallbacks(sig: str) -> list[str]:
    """Signatures to try when a piece set lacks the exact one: the straight run it
    belongs to, then any piece at all."""
    out = [sig or "-"]
    ew, ns = set(sig) & {"E", "W"}, set(sig) & {"N", "S"}
    if ew and not ns:
        out += ["EW", "E", "W"]
    elif ns and not ew:
        out += ["NS", "N", "S"]
    elif ew and ns:
        out += [s for s in ("NESW", "NES", "ESW", "NSW", "NEW") if set(sig) <= set(s)] + ["EW", "NS"]
    else:
        out += ["-", "EW", "NS"]
    seen = []
    for s in out:
        if s not in seen:
            seen.append(s)
    return seen


@dataclass
class Built:
    comps: list[Component] = field(default_factory=list)
    doors: list[dict] = field(default_factory=list)
    storeys: list[dict] = field(default_factory=list)       # z, walls, floor, open, doors, arrivals (local grid)
    notes: list[str] = field(default_factory=list)
    stairs: list[dict] = field(default_factory=list)

    def add(self, item: int, x: int, y: int, z: int, visible: bool = True) -> None:
        if item < 0:                                   # Catalogue.NOTHING: no piece in this cell
            return
        self.comps.append(Component(int(item), x, y, z, visible))
        for e in getattr(item, "extra", ()):
            self.comps.append(Component(int(e), x, y, z, visible))


# --- footprints -------------------------------------------------------------------------------
# A footprint is a union of boxes [x0, y0, x1, y1]. Its walls stand on the union's edge cells;
# its floor fills the cells whose west, north and north-west neighbours are also inside, which
# for one box is x0+1..x1, y0+1..y1 (the originals' grid).

def cells_of(box) -> set[tuple[int, int]]:
    x0, y0, x1, y1 = box
    return {(x, y) for x in range(x0, x1 + 1) for y in range(y0, y1 + 1)}


def region(boxes) -> set[tuple[int, int]]:
    out: set = set()
    for b in boxes:
        out |= cells_of(b)
    return out


def edge(r: set) -> set[tuple[int, int]]:
    return {(x, y) for (x, y) in r
            if any((x + dx, y + dy) not in r for dx in (-1, 0, 1) for dy in (-1, 0, 1))}


def floor_of(r: set) -> set[tuple[int, int]]:
    return {(x, y) for (x, y) in r if (x - 1, y) in r and (x, y - 1) in r and (x - 1, y - 1) in r}


def bbox(cells) -> tuple[int, int, int, int]:
    xs, ys = [c[0] for c in cells], [c[1] for c in cells]
    return min(xs), min(ys), max(xs), max(ys)


def opening_cell(o: dict, box) -> tuple[int, int]:
    """An opening by `at`, or by `side` and `offset` along that side of `box` (the rect it names
    with `rect`, else the whole footprint's bounds)."""
    if "at" in o:
        return tuple(o["at"])
    x0, y0, x1, y1 = box
    side = o["side"]
    off = o.get("offset", ((x1 - x0) if side in "NS" else (y1 - y0)) // 2)
    return {"S": (x0 + off, y1), "N": (x0 + off, y0), "W": (x0, y0 + off), "E": (x1, y0 + off)}[side]


def partition_cells(partitions: list[dict], box) -> set[tuple[int, int]]:
    x0, y0, x1, y1 = box
    cells = set()
    for p in partitions:
        if "x" in p:
            cells |= {(p["x"], y) for y in range(p.get("from", y0), p.get("to", y1) + 1)}
        else:
            cells |= {(x, p["y"]) for x in range(p.get("from", x0), p.get("to", x1) + 1)}
    return cells


def wall_lines(w: int, h: int, partitions: list[dict]) -> set[tuple[int, int]]:
    return edge(cells_of((0, 0, w, h))) | partition_cells(partitions, (0, 0, w, h))


def norm_rects(desc: dict) -> list[dict]:
    n = len(desc["storeys"])
    if "rects" not in desc:
        w, h = desc["size"]
        return [{"box": (0, 0, w, h), "storeys": n, "roof": desc.get("roof")}]
    out = []
    for r in desc["rects"]:
        r = {"box": r} if isinstance(r, list) else dict(r)
        r["box"] = tuple(r["box"])
        r.setdefault("storeys", n)
        r.setdefault("roof", desc.get("roof"))
        if not 1 <= r["storeys"] <= n:
            raise DescriptionError(f"rect {r['box']}: storeys {r['storeys']} outside 1..{n}")
        out.append(r)
    return out


STAIR_STEPS = 4          # four steps of 5 z climb one 20-z storey; a landing of blocks tops the run


def staircase(b: Built, cat: Catalogue, st: dict, mat: str, z: int, floor: set, walls: set,
              steps: int = None, landing: bool = True) -> tuple[set, set]:
    """A straight stair up to the next storey, as the client's houses build it (0x009E): step i
    is a stair piece at z + 5i on i stacked 10-high blocks, then a landing of blocks. Returns
    the cells the next floor must leave open and the cells a climber arrives on. `steps` and
    `landing` shorten it for a rise that is not a whole storey (the climber steps off the last
    step onto a floor up to 4 higher)."""
    steps = STAIR_STEPS if steps is None else steps
    dx, dy = SIDE_STEP[st["rise"]]
    px, py = (1, 0) if st["rise"] in "NS" else (0, 1)
    ax, ay = st["at"]
    width = st.get("width", 1)
    across = [(ax + k * px, ay + k * py) for k in range(width)]
    holes, arrive = set(), set()
    last = steps + (1 if landing else 0)
    for i in range(last + 1):
        row = [(x + i * dx, y + i * dy) for (x, y) in across]
        for c in row:
            if c not in floor or c in walls:
                raise DescriptionError(f"stair from {st['at']} rising {st['rise']}: cell {c} is not open floor")
        if i == last:
            arrive |= set(row)
            break
        holes |= set(row)
        rowset = set(row)
        for (x, y) in row:
            piece = cat.step(mat, st["rise"], signature(rowset, x, y))
            block = cat.block(piece)
            if i < steps:
                for k in range(i):
                    b.add(block, x, y, z + 5 * k)
                b.add(piece, x, y, z + 5 * i)
            else:
                for k in range(steps):
                    b.add(block, x, y, z + 5 * k)
    return holes, arrive


def build(desc: dict, cat: Catalogue, fresh: bool = True) -> tuple[list[Component], dict]:
    if desc.get("format") != 1:
        raise DescriptionError("format must be 1")
    if fresh:
        cat.fresh()
    mats = desc["materials"]
    z0 = desc.get("floor_z", 7)
    step_h = desc.get("storey_height", 20)
    wall_h = desc.get("wall_height", step_h - 1)
    rects = norm_rects(desc)
    storeys = desc["storeys"]
    porches = desc.get("porches", [])
    b = Built()
    house = region(r["box"] for r in rects)
    porch_cells: set = set()
    for p in porches:
        porch_cells |= cells_of(tuple(p["box"])) - house
    holes_next: set = set()
    rails_next: list = []
    arrivals: set = set()
    for n, st in enumerate(storeys):
        z = z0 + n * step_h
        fp = region(r["box"] for r in rects if r["storeys"] > n)
        if not fp:
            raise DescriptionError(f"storey {n}: no rect reaches it")
        box = bbox(fp)
        walls = edge(fp) | partition_cells(st.get("partitions", []), box)
        doors, windows = {}, set()
        for o in st.get("openings", []):
            c = opening_cell(o, rects[o["rect"]]["box"] if "rect" in o else box)
            if c not in walls:
                raise DescriptionError(f"storey {n}: opening {o} at {c} is not on a wall")
            if o["kind"] == "door":
                doors[c] = o
            elif o["kind"] == "window":
                windows.add(c)
            else:
                raise DescriptionError(f"storey {n}: unknown opening kind '{o['kind']}'")
        solid = walls - set(doors)
        for (x, y) in sorted(solid):
            # the pieces beside a door are the wall's run, as the originals build them, not ends
            b.add(cat.wall(mats["wall"], wall_h, signature(walls, x, y), window=(x, y) in windows), x, y, z)
        holes = {tuple(c) for c in st.get("floor_holes", [])} | holes_next
        floor = floor_of(fp) - holes
        ids = cat.floor(st.get("floor", mats["floor"]))
        for (x, y) in sorted(floor):
            b.add(scatter(ids, x, y), x, y, z)
        # a stair's "rail": a low rail round its opening in this floor, the arrival end left open
        for mat, ring in rails_next:
            cells = (ring & floor) - walls
            for (x, y) in sorted(cells):
                b.add(cat.wall(mat, 5, signature(cells, x, y)), x, y, z)
            solid |= cells
        door_item = cat.door(mats["wall"])
        for (x, y), o in sorted(doors.items()):
            along_x = (x - 1, y) in walls or (x + 1, y) in walls
            b.add(door_item, x, y, z, visible=False)
            if (x, y) not in floor:
                # a sill: the floor runs under the south and east walls only, so a door in a north
                # or west wall (or in an upper storey's) has nothing to stand on without one
                b.add(scatter(ids, x, y), x, y, z)
            b.doors.append({"x": x, "y": y, "z": z, "storey": n,
                            "facing": "WestCW" if along_x else "SouthCW",
                            # Plain doors, same art and sounds as the house doors: a BaseHouseDoor
                            # refuses everyone outside a real BaseHouse ("not allowed to access this").
                            "type": "MetalDoor" if o.get("door", mats.get("door", "wood")) == "metal"
                            else "DarkWoodDoor"})
        holes_next = set()
        rails_next = []
        stair_arrivals: set = set()
        for s in st.get("stairs", []):
            if n + 1 >= len(storeys):
                raise DescriptionError(f"storey {n}: a stair leads up from the top storey")
            h, a = staircase(b, cat, s, s.get("material", mats.get("stairs", mats.get("steps", "wooden"))), z,
                             floor, walls)
            holes_next |= h
            stair_arrivals |= a
            if s.get("rail"):
                near = lambda cs: {(x + i, y + j) for (x, y) in cs for i in (-1, 0, 1) for j in (-1, 0, 1)}
                rails_next.append((s["rail"], near(h) - h - near(a)))
            dx, dy = SIDE_STEP[s["rise"]]
            b.stairs.append({"foot": [s["at"][0] - dx, s["at"][1] - dy], "z": z, "cells": sorted(h),
                             "arrive": sorted(a)[0], "to": n + 1})
        b.storeys.append({"z": z, "walls": sorted(solid), "doors": sorted(doors), "windows": sorted(windows),
                          "floor": sorted(floor), "open": sorted(porch_cells) if n == 0 else [],
                          "arrivals": sorted(arrivals)})
        arrivals = stair_arrivals
    # porches: paving off the house, posts at the free corners, and a railed balcony over it
    for p in porches:
        box = tuple(p["box"])
        cells = cells_of(box) - house
        ids = cat.floor(p.get("floor", mats["floor"]))
        for (x, y) in sorted(cells):
            b.add(scatter(ids, x, y), x, y, z0)
        x0, y0, x1, y1 = box
        posts = {c for c in ((x0, y0), (x1, y0), (x0, y1), (x1, y1)) if c not in house}
        bal = p.get("balcony")
        for (x, y) in sorted(posts):
            b.add(cat.post(p.get("posts", mats["wall"]), step_h if bal else wall_h), x, y, z0)
        b.storeys[0]["walls"] = sorted(set(map(tuple, b.storeys[0]["walls"])) | posts)
        if bal:
            if len(b.storeys) < 2:
                raise DescriptionError("a balcony needs a second storey to step out from")
            zb = z0 + step_h
            rail = edge(cells_of(box)) - house
            for (x, y) in sorted(cells):
                b.add(scatter(ids, x, y), x, y, zb)
            for (x, y) in sorted(rail):
                b.add(cat.wall(bal.get("rail", "stone rail"), bal.get("rail_height", 5), signature(rail, x, y)),
                      x, y, zb)
            up = b.storeys[1]
            up["open"] = sorted(set(map(tuple, up["open"])) | (cells - rail))
            up["walls"] = sorted(set(map(tuple, up["walls"])) | rail)
    ground_cells = house | porch_cells
    if "foundation" in mats:
        ring = edge(ground_cells)
        for (x, y) in sorted(ring):
            for dz, item in cat.course(mats["foundation"], 5, signature(ring, x, y)):
                b.add(item, x, y, z0 - 7 + dz)
    # entrance steps: outside each ground door that opens straight onto open ground, and at each
    # porch's entry
    entries = []
    for (x, y) in b.storeys[0]["doors"]:
        for side, (dx, dy) in SIDE_STEP.items():
            if (x + dx, y + dy) not in ground_cells and (x - dx, y - dy) in house:
                entries.append((x, y, side))
    for p in porches:
        if "entry" in p:
            entries.append((*opening_cell(p["entry"], tuple(p["box"])), p["entry"]["side"]))
    step_rows = []
    if "steps" in mats:
        for (x, y, side) in entries:
            dx, dy = SIDE_STEP[side]
            across = [(-1, 0), (0, 0), (1, 0)] if side in "NS" else [(0, -1), (0, 0), (0, 1)]
            row = {(x + dx + ax, y + dy + ay) for ax, ay in across}
            step_rows.append((x + dx, y + dy, side))
            for (sx, sy) in sorted(row):
                b.add(cat.step(mats["steps"], TOWARD[side], signature(row, sx, sy)), sx, sy, z0 - 5)
    # roofs, one per rect at its own top: where two cover a cell the higher wins, and nothing is
    # roofed inside a taller rect (its upper walls stand there)
    roof_cells: dict = {}
    roof_z = None
    for i, r in enumerate(rects):
        if not r["roof"]:
            continue
        top = z0 + r["storeys"] * step_h
        roof_z = top if roof_z is None else min(roof_z, top)
        rb = Built()
        roof_rect(rb, cat, r["roof"], mats, r["box"], top)
        taller = region(q["box"] for q in rects if q["storeys"] > r["storeys"])
        # where this roof's parapet runs into a taller rect's wall, its last piece (on that wall's
        # cell) carries the parapet up to the wall's face: kept, or the parapet stops a cell short
        ring = edge(cells_of(r["box"]))
        junction = {(x, y) for (x, y) in ring & taller
                    if any((x + dx, y + dy) in ring - taller for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))}
        for c in rb.comps:
            if (c.x, c.y) not in taller:
                roof_cells.setdefault((c.x, c.y), {}).setdefault(i, []).append(c)
            elif (c.x, c.y) in junction and cat.pieces.get(f"{c.item:#06x}", {}).get("role") in ("wall", "post"):
                b.comps.append(c)
    for cell, by_rect in sorted(roof_cells.items()):
        b.comps.extend(max(by_rect.values(), key=lambda cs: max(c.z for c in cs)))
    yard = desc.get("yard")
    yard_side = build_yard(b, cat, yard, z0 - 7, ground_cells, step_rows) if yard else None
    for d in desc.get("decor", []):
        dz = d.get("z", 0) + (b.storeys[d["storey"]]["z"] if "storey" in d else z0 - 7)
        b.add(int(d["item"], 16) if isinstance(d["item"], str) else d["item"], d["at"][0], d["at"][1], dz)
    x0, y0, x1, y1 = bbox(house)
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    stops = walk_stops(b, entries, yard_side, porches, house, (cx, cy), z0, step_h)
    b.add(CENTRE_MARKER, cx, cy, 0, visible=False)
    comps = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in b.comps]
    doors = [dict(d, x=d["x"] - cx, y=d["y"] - cy) for d in b.doors]
    side = {"format": 1, "name": desc["name"], "size": [x1 - x0, y1 - y0], "centre": [cx, cy],
            "storeys": [s["z"] for s in b.storeys], "roof_z": roof_z,
            "doors": doors, "components": len(comps), "notes": b.notes,
            "stops": [dict(t, x=t["x"] - cx, y=t["y"] - cy) for t in stops],
            "local": {"storeys": b.storeys, "yard": yard_side, "stairs": b.stairs}}
    return comps, side


def walk_stops(b: Built, entries: list, yard: dict | None, porches: list, house: set, centre, z0: int,
               step_h: int) -> list[dict]:
    """Where a proof walks, in order: up to the house (through the yard's gate), onto the
    entrance step, in, to the middle of the ground floor, and for each stair its foot, the
    floor it arrives on, and a balcony off that floor. z is local, as the doors' are."""
    stops = []
    if not entries:
        return stops
    x, y, side = entries[0]
    if yard and yard["steps"]:
        # the entrance the yard's path leads to
        inner = [e for e in entries if [e[0] + SIDE_STEP[e[2]][0], e[1] + SIDE_STEP[e[2]][1]] in yard["steps"]
                 or (e[0] + SIDE_STEP[e[2]][0], e[1] + SIDE_STEP[e[2]][1]) in set(map(tuple, yard["steps"]))]
        x, y, side = (inner or entries)[0]
    dx, dy = SIDE_STEP[side]
    if yard:
        gx, gy = yard["gate"]
        gx0, gy0, gx1, gy1 = yard["box"]
        ox, oy = (0, 2) if gy == gy1 else (0, -2) if gy == gy0 else (2, 0) if gx == gx1 else (-2, 0)
        stops.append({"name": "outside", "x": gx + ox, "y": gy + oy, "z": z0 - 7})
        stops.append({"name": "yard", "x": gx - ox // 2, "y": gy - oy // 2, "z": z0 - 7})
    else:
        stops.append({"name": "front", "x": x + 3 * dx, "y": y + 3 * dy, "z": z0 - 7})
    stops.append({"name": "step", "x": x + dx, "y": y + dy, "z": z0 - 5})
    stops.append({"name": "entrance", "x": x, "y": y, "z": z0})
    stair_cells = {tuple(c) for st in b.stairs for c in st["cells"]}
    stair_cells |= {tuple(c) for st in b.storeys for c in st["doors"]}      # nor stop in a doorway

    def nearest(cells, at):
        cells = [c for c in cells if c not in stair_cells]
        return min(cells, key=lambda c: (abs(c[0] - at[0]) + abs(c[1] - at[1]), c)) if cells else None

    g = b.storeys[0]
    walk = set(map(tuple, g["floor"])) - set(map(tuple, g["walls"]))
    inside = nearest(walk, centre)
    if inside:
        stops.append({"name": "inside", "x": inside[0], "y": inside[1], "z": z0})
    for st in b.stairs:
        up = b.storeys[st["to"]]
        stops.append({"name": f"stair{st['to']}_foot", "x": st["foot"][0], "y": st["foot"][1], "z": st["z"]})
        stops.append({"name": f"storey{st['to']}", "x": st["arrive"][0], "y": st["arrive"][1], "z": up["z"]})
        upwalk = set(map(tuple, up["floor"])) - set(map(tuple, up["walls"]))
        far = nearest(upwalk, centre)
        if far and list(far) != st["arrive"]:
            stops.append({"name": f"storey{st['to']}_room", "x": far[0], "y": far[1], "z": up["z"]})
        balcony = sorted(set(map(tuple, up.get("open", []))))
        if balcony:
            out_doors = [d for d in map(tuple, up["doors"])
                         if any((d[0] + ex, d[1] + ey) in set(balcony) for ex, ey in SIDE_STEP.values())]
            if out_doors:
                stops.append({"name": f"storey{st['to']}_balcony_door", "x": out_doors[0][0], "y": out_doors[0][1],
                              "z": up["z"]})
            c = balcony[len(balcony) // 2]
            stops.append({"name": f"storey{st['to']}_balcony", "x": c[0], "y": c[1], "z": up["z"]})
    return stops


def build_yard(b: Built, cat: Catalogue, yard: dict, z: int, taken: set, step_rows: list) -> dict:
    """A fence on the edge of the yard's box (not through the house: a courtyard is closed by
    its walls) with a gate, a real gate door, and a path of paving from the gate to the
    house's entrance steps."""
    box = tuple(yard["box"])
    ring = edge(cells_of(box))
    gate = opening_cell(yard["gate"], box)
    if gate not in ring:
        raise DescriptionError(f"yard gate {gate} is not on the fence")
    if gate in taken:
        raise DescriptionError(f"yard gate {gate} is in the house")
    fence = ring - {gate} - taken          # where the box meets the house, its walls close the yard
    mat = yard.get("fence", "wooden fence")
    for (x, y) in sorted(fence):
        b.add(cat.wall(mat, yard.get("height", 11), signature(fence, x, y)), x, y, z)
    along_x = (gate[0] - 1, gate[1]) in ring
    b.doors.append({"x": gate[0], "y": gate[1], "z": z, "storey": None, "facing": "WestCW" if along_x else "SouthCW",
                    "type": yard.get("gate_type", "IronGate" if "iron" in mat else "LightWoodGate")})
    inside = cells_of(box) - ring
    steps = {(x, y) for (x, y, _) in step_rows if (x, y) in inside}      # the entrances inside the fence
    path = []
    if yard.get("path") and steps:
        # breadth first from the cell inside the gate to the nearest entrance step, round the house
        gx, gy = gate
        start = next(c for c in ((gx, gy - 1), (gx, gy + 1), (gx - 1, gy), (gx + 1, gy)) if c in inside)
        prev, queue, goal = {start: None}, [start], None
        while queue and goal is None:
            nq = []
            for c in queue:
                for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
                    nc = (c[0] + dx, c[1] + dy)
                    if nc in prev or nc not in inside or nc in taken:
                        continue
                    prev[nc] = c
                    if nc in steps:
                        goal = nc
                        break
                    nq.append(nc)
                if goal:
                    break
            queue = nq
        c = prev.get(goal) if goal else None
        while c is not None:
            path.append(c)
            c = prev[c]
        ids = cat.floor(yard["path"])
        for (x, y) in sorted(path):
            b.add(scatter(ids, x, y), x, y, z)
    return {"fence": sorted(fence), "gate": list(gate), "box": list(box), "steps": sorted(steps), "path": sorted(path)}


def roof_rect(b: Built, cat: Catalogue, roof: dict, mats: dict, box, top: int) -> None:
    style = roof.get("style", "gable")
    x0, y0, x1, y1 = box
    if style == "gable":
        roof_gable(b, cat, roof, mats, x1 - x0, y1 - y0, top, x0, y0)
    elif style == "flat":
        r = cells_of(box)
        ids = cat.floor(roof.get("material", mats["floor"]))
        for (x, y) in sorted(floor_of(r)):
            b.add(scatter(ids, x, y), x, y, top)
        if roof.get("parapet"):
            # parapet_gaps: edge cells left open (where an outside stair steps onto the roof),
            # floored at the roof's height instead
            gaps = {tuple(c) for c in roof.get("parapet_gaps", [])} & edge(r)
            for (x, y) in sorted(gaps - floor_of(r)):
                b.add(scatter(ids, x, y), x, y, top)
            ring = edge(r) - gaps
            for (x, y) in sorted(ring):
                for dz, item in cat.course(roof["parapet"], roof.get("parapet_height", 6), signature(ring, x, y)):
                    b.add(item, x, y, top + dz)
    else:
        raise DescriptionError(f"roof style '{style}' is not supported (gable, flat)")


def roof_gable(b: Built, cat: Catalogue, roof: dict, mats: dict, w: int, h: int, top: int,
               ox: int = 0, oy: int = 0) -> None:
    """A gable roof over x 1..W+1, y 1..H+1 (the originals' overhang), 3 z a course,
    with gable-end fill of the wall material on both ends. The originals fill only the end
    the client shows (south or east); ours are complete on every side, so the data holds the
    whole building (for a 3D build of it, say)."""
    mat = roof.get("material", mats["roof"])
    ridge = roof.get("ridge", "y" if h >= w else "x")
    span = (w if ridge == "y" else h) + 1
    if span % 2 == 0:
        raise DescriptionError(f"a gable roof with its ridge along {ridge} needs an even "
                               f"{'width' if ridge == 'y' else 'depth'} (the originals' ridge sits on one tile)")
    length = range(1, (h if ridge == "y" else w) + 2)
    fill_lines = (0, h) if ridge == "y" else (0, w)
    lo, hi, k = 1, span, 0
    while lo <= hi:
        z = top + 3 * k
        if lo == hi:
            for t in length:
                x, y = (lo, t) if ridge == "y" else (t, lo)
                b.add(cat.roof(mat, "ridge_y" if ridge == "y" else "ridge_x"), ox + x, oy + y, z)
        else:
            a_side, b_side = ("W", "E") if ridge == "y" else ("N", "S")
            for t in length:
                ax, ay = (lo, t) if ridge == "y" else (t, lo)
                bx, by = (hi, t) if ridge == "y" else (t, hi)
                b.add(cat.roof(mat, a_side), ox + ax, oy + ay, z)
                b.add(cat.roof(mat, b_side), ox + bx, oy + by, z)
            for line in fill_lines:
                fill = {((u, line) if ridge == "y" else (line, u)) for u in range(lo + 1, hi)}
                for (fx, fy) in sorted(fill):
                    b.add(cat.wall(mats["wall"], 3, signature(fill, fx, fy)), ox + fx, oy + fy, z)
        lo, hi, k = lo + 1, hi - 1, k + 1
