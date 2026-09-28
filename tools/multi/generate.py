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


class Catalogue:
    def __init__(self, folder: Path):
        self.fam = json.loads((folder / "families.json").read_text(encoding="utf-8"))
        self.pieces = json.loads((folder / "pieces.json").read_text(encoding="utf-8"))
        self.context: collections.Counter = collections.Counter()

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
            for s in fallbacks(sig):
                cands = [i for r in roles for i in fam.get(r, {}).get(str(h), {}).get(s, [])]
                if cands:
                    return self.choose(cands)
        raise DescriptionError(f"material '{mat}' has no {roles[0]} piece near height {height} for '{sig or '-'}'")

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
        fam = self.material(mat).get("stair", {})
        for s in fallbacks(sig):
            cands = fam.get(f"{ascent}/{s or '-'}")
            if cands:
                return self.choose(cands)
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
    storeys: list[dict] = field(default_factory=list)       # z, walls, floor, doors per storey (local grid)
    notes: list[str] = field(default_factory=list)

    def add(self, item: int, x: int, y: int, z: int, visible: bool = True, why: str = "") -> None:
        self.comps.append(Component(item, x, y, z, visible))


def opening_cell(o: dict, w: int, h: int) -> tuple[int, int]:
    if "at" in o:
        return tuple(o["at"])
    side, off = o["side"], o.get("offset", (w if o["side"] in "NS" else h) // 2)
    return {"S": (off, h), "N": (off, 0), "W": (0, off), "E": (w, off)}[side]


def wall_lines(w: int, h: int, partitions: list[dict]) -> set[tuple[int, int]]:
    cells = {(x, y) for x in range(w + 1) for y in (0, h)} | {(x, y) for y in range(h + 1) for x in (0, w)}
    for p in partitions:
        if "x" in p:
            cells |= {(p["x"], y) for y in range(p.get("from", 0), p.get("to", h) + 1)}
        else:
            cells |= {(x, p["y"]) for x in range(p.get("from", 0), p.get("to", w) + 1)}
    return cells


def build(desc: dict, cat: Catalogue) -> tuple[list[Component], dict]:
    if desc.get("format") != 1:
        raise DescriptionError("format must be 1")
    w, h = desc["size"]
    mats = desc["materials"]
    z0 = desc.get("floor_z", 7)
    step_h = desc.get("storey_height", 20)
    wall_h = desc.get("wall_height", step_h - 1)
    b = Built()
    storeys = desc["storeys"]
    for n, st in enumerate(storeys):
        z = z0 + n * step_h
        walls = wall_lines(w, h, st.get("partitions", []))
        doors, windows = {}, set()
        for o in st.get("openings", []):
            c = opening_cell(o, w, h)
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
            b.add(cat.wall(mats["wall"], wall_h, signature(solid, x, y), window=(x, y) in windows), x, y, z)
        holes = {tuple(c) for c in st.get("floor_holes", [])}
        floor = [(x, y) for x in range(1, w + 1) for y in range(1, h + 1) if (x, y) not in holes]
        ids = cat.floor(mats["floor"])
        for (x, y) in floor:
            b.add(ids[(x * 7 + y * 13) % len(ids)], x, y, z)
        door_item = cat.door(mats["wall"])
        for (x, y), o in sorted(doors.items()):
            along_x = (x - 1, y) in walls or (x + 1, y) in walls
            b.add(door_item, x, y, z, visible=False)
            b.doors.append({"x": x, "y": y, "z": z, "storey": n,
                            "facing": "WestCW" if along_x else "SouthCW",
                            # Plain doors, same art and sounds as the house doors: a BaseHouseDoor
                            # refuses everyone outside a real BaseHouse ("not allowed to access this").
                            "type": "MetalDoor" if o.get("door", mats.get("door", "wood")) == "metal"
                            else "DarkWoodDoor"})
        b.storeys.append({"z": z, "walls": sorted(solid), "doors": sorted(doors), "windows": sorted(windows),
                          "floor": floor})
    if "foundation" in mats:
        fz = z0 - 7
        ring = wall_lines(w, h, [])
        for (x, y) in sorted(ring):
            b.add(cat.wall(mats["foundation"], 5, signature(ring, x, y)), x, y, fz)
    ground = b.storeys[0]
    for (x, y) in ground["doors"]:
        side = "S" if y == h else "N" if y == 0 else "E" if x == w else "W" if x == 0 else None
        if side is None or "steps" not in mats:
            continue
        dx, dy = SIDE_STEP[side]
        across = [(-1, 0), (0, 0), (1, 0)] if side in "NS" else [(0, -1), (0, 0), (0, 1)]
        row = {(x + dx + ax, y + dy + ay) for ax, ay in across}
        for (sx, sy) in sorted(row):
            b.add(cat.step(mats["steps"], TOWARD[side], signature(row, sx, sy)), sx, sy, z0 - 5)
    top = z0 + len(storeys) * step_h
    roof = desc.get("roof")
    if roof:
        roof_gable(b, cat, roof, mats, w, h, top)
    cx, cy = w // 2, h // 2
    b.add(CENTRE_MARKER, cx, cy, 0, visible=False)
    comps = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in b.comps]
    doors = [dict(d, x=d["x"] - cx, y=d["y"] - cy) for d in b.doors]
    side = {"format": 1, "name": desc["name"], "size": [w, h], "centre": [cx, cy],
            "storeys": [s["z"] for s in b.storeys], "roof_z": top if roof else None,
            "doors": doors, "components": len(comps), "notes": b.notes,
            "local": {"storeys": [{k: v for k, v in s.items()} for s in b.storeys]}}
    return comps, side


def roof_gable(b: Built, cat: Catalogue, roof: dict, mats: dict, w: int, h: int, top: int) -> None:
    """A gable roof over x 1..W+1, y 1..H+1 (the originals' overhang), 3 z a course,
    with gable-end fill of the wall material on the visible (south or east) end."""
    if roof.get("style", "gable") != "gable":
        raise DescriptionError(f"roof style '{roof.get('style')}' is not supported yet")
    mat = mats["roof"]
    ridge = roof.get("ridge", "y" if h >= w else "x")
    span = (w if ridge == "y" else h) + 1
    if span % 2 == 0:
        raise DescriptionError(f"a gable roof with its ridge along {ridge} needs an even "
                               f"{'width' if ridge == 'y' else 'depth'} (the originals' ridge sits on one tile)")
    length = range(1, (h if ridge == "y" else w) + 2)
    fill_line = h if ridge == "y" else w
    lo, hi, k = 1, span, 0
    while lo <= hi:
        z = top + 3 * k
        if lo == hi:
            for t in length:
                x, y = (lo, t) if ridge == "y" else (t, lo)
                b.add(cat.roof(mat, "ridge_y" if ridge == "y" else "ridge_x"), x, y, z)
        else:
            a_side, b_side = ("W", "E") if ridge == "y" else ("N", "S")
            for t in length:
                ax, ay = (lo, t) if ridge == "y" else (t, lo)
                bx, by = (hi, t) if ridge == "y" else (t, hi)
                b.add(cat.roof(mat, a_side), ax, ay, z)
                b.add(cat.roof(mat, b_side), bx, by, z)
            fill = {((u, fill_line) if ridge == "y" else (fill_line, u)) for u in range(lo + 1, hi)}
            for (fx, fy) in sorted(fill):
                b.add(cat.wall(mats["wall"], 3, signature(fill, fx, fy)), fx, fy, z)
        lo, hi, k = lo + 1, hi - 1, k + 1
