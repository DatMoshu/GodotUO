"""Scenes: fortifications and buildings composed on one grid, split into multis that join.

A scene (docs/data_formats.md, "Scenes") lists elements on one local grid:

  wall      a band of cells along a path (thickness, base z, top z): stacked wall pieces on
            its faces, a walkway floor on top, a crenellated parapet on its outer side, and
            gates (a passage through, with doors) and culverts (a passage, no doors)
  tower     a round or square ring of walls with a floor at each level, a straight stair up
            from each level to the next, and a crenellated top
  platform  raised ground: floor at a z over a region, with stone faces down to the base
  causeway  a raised road along a path: a platform band with rails, and stairs at its foot
  stair     a straight flight, 5 z a step, on stacked blocks
  house     any house description (generate.py), moved to a spot and a z

Elements carve each other by rank (house > tower > wall > causeway > platform): a cell a
higher element stands on loses what lower ones put there. Where a wall's walkway meets a
tower at one of its levels, the tower's ring opens there.

The whole scene is built once, on one grid, then cut into parts (each element names its
part; a part too big for one multi is cut again on a grid), so pieces meet exactly: every
part is placed at the scene's origin plus its own centre.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import generate as G  # noqa: E402
from generate import SIDE_STEP, Catalogue, DescriptionError, signature  # noqa: E402
from multifile import MAX_COMPONENTS, Component  # noqa: E402

RANK = {"platform": 0, "causeway": 1, "stair": 2, "wall": 2, "tower": 3, "house": 4}
HEIGHTS = (20, 10, 5, 3)


def courses(z0: int, z1: int, cuts=()) -> list[tuple[int, int]]:
    """(z, height) pieces stacking z0 up to z1, breaking at every cut (an opening's sill or head)."""
    marks = sorted({z0, z1} | {c for c in cuts if z0 < c < z1})
    out = []
    for a, b in zip(marks, marks[1:]):
        z = a
        while b - z >= 3:
            h = next(h for h in HEIGHTS if h <= b - z)
            out.append((z, h))
            z += h
    return out


def line_cells(p, q) -> list[tuple[int, int]]:
    """A 4-connected run of cells from p to q (diagonals become UO's stepped walls)."""
    (x0, y0), (x1, y1) = p, q
    cells = [(x0, y0)]
    dx, dy = x1 - x0, y1 - y0
    n = max(abs(dx), abs(dy))
    x, y = x0, y0
    for i in range(1, n + 1):
        tx, ty = x0 + round(dx * i / n), y0 + round(dy * i / n)
        if tx != x and ty != y:
            cells.append((tx, y))           # step across first, so the run stays 4-connected
        x, y = tx, ty
        cells.append((x, y))
    return cells


def path_cells(path) -> list[tuple[int, int]]:
    out = []
    for p, q in zip(path, path[1:]):
        for c in line_cells(tuple(p), tuple(q)):
            if not out or out[-1] != c:
                out.append(c)
    return out


def arc_path(cx, cy, r, a0, a1, step=None) -> list[list[int]]:
    """Points on a circle from angle a0 to a1 (degrees; 0 is east, 90 is south on the grid)."""
    step = step or max(4, int(abs(a1 - a0) / 6))
    return [[round(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / step))),
             round(cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / step)))] for i in range(step + 1)]


def band(path, thickness: int) -> tuple[set, dict]:
    """The cells within thickness // 2 of the path, and for each which side of the path it lies
    on (+1 left of travel, -1 right, 0 on it)."""
    centre = path_cells(path)
    half = thickness // 2
    cells, side = set(), {}
    for i, (x, y) in enumerate(centre):
        a, b = centre[max(i - 1, 0)], centre[min(i + 1, len(centre) - 1)]
        tx, ty = b[0] - a[0], b[1] - a[1]
        for ox in range(-half, half + 1):
            for oy in range(-half, half + 1):
                c = (x + ox, y + oy)
                cells.add(c)
                s = tx * oy - ty * ox          # cross product: > 0 right of travel on a y-down grid
                v = 0 if (ox, oy) == (0, 0) else (-1 if s > 0 else 1 if s < 0 else side.get(c, 0))
                if c not in side or side[c] == 0:
                    side[c] = v
    return cells, side


def shape_cells(shape: dict) -> set:
    if "box" in shape:
        return G.cells_of(tuple(shape["box"]))
    if "disc" in shape:
        cx, cy, r = shape["disc"]
        return {(x, y) for x in range(cx - r - 1, cx + r + 2) for y in range(cy - r - 1, cy + r + 2)
                if (x - cx) ** 2 + (y - cy) ** 2 <= (r + 0.5) ** 2}
    if "band" in shape:
        return band(shape["band"], shape.get("thickness", 3))[0]
    raise DescriptionError(f"unknown shape {shape}")


def region_of(el: dict) -> set:
    cells = set()
    for s in el.get("shapes", []):
        cells |= shape_cells(s)
    for s in el.get("minus", []):
        cells -= shape_cells(s)
    return cells


class Scene:
    def __init__(self, desc: dict, cat: Catalogue):
        self.desc, self.cat = desc, cat
        self.mats = desc.get("materials", {})
        self.items: list[tuple[Component, int, str]] = []        # component, rank, part
        self.claims: dict[tuple[int, int], int] = {}              # cell -> highest rank standing on it
        self.doors: list[dict] = []
        self.walks: list[dict] = []                                # wall walkways: cells, z
        self.flights: list[dict] = []                              # every stair: foot, arrive, z_from, z_to
        self.notes: list[str] = []

    def add(self, item, x, y, z, rank, part, visible=True):
        self.items.append((Component(item, x, y, z, visible), rank, part))

    def claim(self, cells, rank):
        for c in cells:
            if self.claims.get(c, -1) < rank:
                self.claims[c] = rank

    def mat(self, el, key, default=None):
        return el.get(key) or self.mats.get(key) or default

    def floor_ids(self, mat):
        return self.cat.floor(mat)

    def paint_floor(self, cells, z, mat, rank, part):
        ids = self.floor_ids(mat)
        for (x, y) in sorted(cells):
            self.add(ids[(x * 7 + y * 13) % len(ids)], x, y, z, rank, part)

    def stack(self, cells, z0, z1, mat, rank, part, cuts=(), openings=()):
        """Wall pieces on `cells` from z0 to z1; `openings` are (cells, z_from, z_to) left open."""
        for z, h in courses(z0, z1, cuts):
            solid = set(cells)
            for oc, a, b in openings:
                if a <= z < b:
                    solid -= set(oc)
            for (x, y) in sorted(solid):
                self.add(self.cat.wall(mat, h, signature(solid, x, y)), x, y, z, rank, part)

    def parapet(self, cells, z, mat, rank, part, crenels=True):
        cells = set(cells)
        for (x, y) in sorted(cells):
            self.add(self.cat.wall(mat, 5, signature(cells, x, y)), x, y, z, rank, part)
        if crenels:
            merlons = {(x, y) for (x, y) in cells if (x + y) % 2 == 0}
            for (x, y) in sorted(merlons):
                self.add(self.cat.wall(mat, 5, "-"), x, y, z + 5, rank, part)

    def flight(self, at, rise, z_from, z_to, width, mat, rank, part):
        """A straight stair: step i a stair piece at z_from + 5i on i blocks, then a landing."""
        if (z_to - z_from) % 5:
            raise DescriptionError(f"a stair from z {z_from} to {z_to}: the rise is not a multiple of 5")
        n = (z_to - z_from) // 5
        dx, dy = SIDE_STEP[rise]
        px, py = (1, 0) if rise in "NS" else (0, 1)
        across = [(at[0] + k * px, at[1] + k * py) for k in range(width)]
        cells = set()
        for i in range(n):
            row = {(x + i * dx, y + i * dy) for (x, y) in across}
            for (x, y) in sorted(row):
                piece = self.cat.step(mat, rise, signature(row, x, y))
                block = self.cat.block(piece)
                for k in range(i):
                    self.add(block, x, y, z_from + 5 * k, rank, part)
                self.add(piece, x, y, z_from + 5 * i, rank, part)
            cells |= row
        landing = {(x + n * dx, y + n * dy) for (x, y) in across}
        for (x, y) in sorted(landing):
            block = self.cat.block(self.cat.step(mat, rise, signature(landing, x, y)))
            for k in range(n):                     # the top block stands a walker at z_to
                self.add(block, x, y, z_from + 5 * k, rank, part)
        arrive = {(x + (n + 1) * dx, y + (n + 1) * dy) for (x, y) in across}
        self.flights.append({"foot": [across[0][0] - dx, across[0][1] - dy], "arrive": list(min(landing)),    # stands at z_to whichever way it is left
                             "z_from": z_from, "z_to": z_to})
        return cells | landing, arrive

    # --- elements -------------------------------------------------------------------------------

    def wall(self, el, part):
        rank = RANK["wall"]
        cells, side = band(el["path"], el.get("thickness", 3))
        base, top = el.get("z", 0), el["top"]
        mat = self.mat(el, "wall", "stone")
        outer = {"left": 1, "right": -1}.get(el.get("outer", "left"), 1)
        openings, cuts = [], set()
        for g in el.get("gates", []):
            gc = shape_cells(g) & cells if "box" in g or "disc" in g else set(map(tuple, g["cells"]))
            gz = g.get("z", base)
            gh = gz + g.get("height", 20)
            if gh >= top:
                raise DescriptionError(f"gate {g} reaches the walkway ({gh} >= {top})")
            openings.append((gc, gz, gh))
            cuts |= {gz, gh}
            self.paint_floor(gc, gz, self.mat(g, "floor", self.mats.get("paving", "flagstones")), rank, part)
            if g.get("door"):
                self.gate_doors(g, gc, gz, part)
        # faces: the band's edge, and round each passage the band's edge with the passage taken out
        faces = G.edge(cells)
        for z, h in courses(base, top, cuts):
            gone = set()
            for gc, a, b in openings:
                if a <= z < b:
                    gone |= gc
            solid = G.edge(cells - gone) if gone else faces
            for (x, y) in sorted(solid):
                self.add(self.cat.wall(mat, h, signature(solid, x, y)), x, y, z, rank, part)
        self.paint_floor(cells, top, self.mat(el, "walk", self.mats.get("walk", "stone")), rank, part)
        para = {c for c in faces if side.get(c, 0) == outer}
        if el.get("parapet", "outer") == "both":
            para = {c for c in faces if side.get(c, 0) != 0}
        para -= {tuple(c) for c in el.get("parapet_gaps", [])}      # where a stair steps onto the walk
        if el.get("parapet", "outer") != "none":
            self.parapet(para, top, self.mat(el, "parapet", mat), rank, part, el.get("crenels", True))
        self.walks.append({"cells": cells - para, "z": top})
        self.claim(cells, rank)

    def gate_doors(self, g, gc, z, part):
        """Doors across a passage, on its outer line: a pair meeting in the middle when two wide."""
        line = g.get("door_line")
        if line is None:
            raise DescriptionError(f"gate {g}: say where its doors hang with door_line [[x, y], ...]")
        line = [tuple(c) for c in line]
        along_x = len({c[1] for c in line}) == 1
        kind = g["door"]
        if along_x:
            facings = ["WestCW", "EastCCW"] if len(line) == 2 else ["WestCW"] * len(line)
        else:
            facings = ["SouthCW", "NorthCCW"] if len(line) == 2 else ["SouthCW"] * len(line)
        for c, f in zip(sorted(line), facings):
            self.doors.append({"x": c[0], "y": c[1], "z": z, "facing": f, "type": kind, "part": part})

    def tower(self, el, part):
        rank = RANK["tower"]
        disc = shape_cells(el)
        ring = G.edge(disc)
        inside = disc - ring
        base, levels, top = el.get("z", 0), sorted(el.get("levels", [])), el["top"]
        mat = self.mat(el, "wall", "stone")
        openings, cuts = [], set(levels)
        for d in el.get("doors", []):
            c = {tuple(d["at"])}
            openings.append((c, d["z"], d["z"] + d.get("height", 20)))
            cuts |= {d["z"], d["z"] + d.get("height", 20)}
            self.paint_floor(c, d["z"], self.mat(el, "floor", "stone"), rank, part)
            if d.get("door"):
                along_x = (d["at"][0] - 1, d["at"][1]) in ring and (d["at"][0] + 1, d["at"][1]) in ring
                self.doors.append({"x": d["at"][0], "y": d["at"][1], "z": d["z"],
                                   "facing": "WestCW" if along_x else "SouthCW", "type": d["door"], "part": part})
        # where a wall's walkway reaches the ring at a level, open it
        for w in self.walks:
            if w["z"] in levels or w["z"] == top:
                meet = ring & w["cells"]
                if meet:
                    h = 20 if w["z"] != top else 0
                    if h:
                        openings.append((meet, w["z"], w["z"] + h))
                        cuts |= {w["z"], w["z"] + h}
                    self.paint_floor(meet, w["z"], self.mat(el, "floor", "stone"), rank, part)
        self.stack(ring, base, top, mat, rank, part, cuts, openings)
        holes: set = set()
        rows = sorted({y for (_, y) in inside})
        cy = (rows[0] + rows[-1]) // 2 if rows else 0
        for k, z in enumerate(levels):
            floor = inside - holes
            self.paint_floor(floor, z, self.mat(el, "floor", "stone"), rank, part)
            holes = set()
            nxt = levels[k + 1] if k + 1 < len(levels) else top
            if el.get("stairs", True) and nxt - z == 20:
                # rows cy-1, cy, cy+1 in turn, rising E then W, so no flight stands over another
                # below it and each arrival is beside the next foot
                row_y = cy - 1 + k % 3
                row = sorted(x for (x, y) in inside if y == row_y)
                if len(row) < 7:            # a foot, four steps, a landing, and a cell to step off onto
                    raise DescriptionError(f"tower {el.get('part')}: too narrow for a stair (row of {len(row)})")
                start, rise = ((row[1], row_y), "E") if k % 2 == 0 else ((row[-2], row_y), "W")
                used, arrive = self.flight(start, rise, z, nxt, 1, self.mat(el, "stairs", "stone"), rank, part)
                holes = used
        self.paint_floor(disc - holes, top, self.mat(el, "floor", "stone"), rank, part)
        if el.get("parapet", True):
            meets = set().union(*[ring & w["cells"] for w in self.walks if w["z"] == top]) if self.walks else set()
            self.parapet(ring - meets, top, self.mat(el, "parapet", mat), rank, part, el.get("crenels", True))
        self.claim(disc, rank)

    def platform(self, el, part):
        rank = RANK["platform"]
        cells = region_of(el)
        base, z = el.get("base", 0), el["z"]
        self.paint_floor(cells, z, self.mat(el, "floor", "flagstones"), rank, part)
        if el.get("face", True):
            self.stack(G.edge(cells), base, z, self.mat(el, "face", self.mats.get("wall", "stone")), rank, part)
        self.claim(cells, rank)

    def causeway(self, el, part):
        rank = RANK["causeway"]
        cells, _ = band(el["path"], el.get("width", 3) + 2)
        base, z = el.get("base", 0), el["z"]
        rim = G.edge(cells)
        ends = set(path_cells(el["path"])[:1]) | set(path_cells(el["path"])[-1:])
        end_rows = {c for c in rim if any(abs(c[0] - e[0]) + abs(c[1] - e[1]) <= el.get("width", 3) for e in ends)}
        self.paint_floor(cells, z, self.mat(el, "floor", "flagstones"), rank, part)
        self.stack(rim, base, z, self.mat(el, "face", self.mats.get("wall", "stone")), rank, part)
        rail = rim - end_rows
        for (x, y) in sorted(rail):
            self.add(self.cat.wall(self.mat(el, "rail", "stone rail"), 5, signature(rail, x, y)), x, y, z, rank, part)
        self.claim(cells, rank)

    def stair(self, el, part):
        cells, _ = self.flight(tuple(el["at"]), el["rise"], el.get("z", 0), el["to"], el.get("width", 1),
                               self.mat(el, "stairs", "stone"), RANK["stair"], part)
        self.claim(cells, RANK["stair"])

    def house(self, el, part):
        comps, side = G.build(el["desc"], self.cat)
        ox, oy = el["at"]
        oz = el.get("z", 0)
        cx, cy = side["centre"]
        for c in comps:
            if c.item == G.CENTRE_MARKER and not c.visible:
                continue
            self.add(c.item, c.x + cx + ox, c.y + cy + oy, c.z + oz, RANK["house"], part, c.visible)
        for d in side["doors"]:
            self.doors.append({"x": d["x"] + cx + ox, "y": d["y"] + cy + oy, "z": d["z"] + oz,
                               "facing": d["facing"], "type": d["type"], "part": part})
        zs = [st["z"] for st in side["local"]["storeys"]]
        for st in side["local"]["stairs"]:
            self.flights.append({"foot": [st["foot"][0] + ox, st["foot"][1] + oy],
                                 "arrive": [st["arrive"][0] + ox, st["arrive"][1] + oy],
                                 "z_from": st["z"] + oz, "z_to": zs[st["to"]] + oz})
        cells = set()
        for st in side["local"]["storeys"]:
            cells |= set(map(tuple, st["walls"])) | set(map(tuple, st["floor"]))
        self.claim({(x + ox, y + oy) for (x, y) in cells}, RANK["house"])

    def build(self) -> list[dict]:
        for n, el in enumerate(self.desc["elements"]):
            kind = el["type"]
            if kind not in RANK:
                raise DescriptionError(f"element {n}: unknown type '{kind}'")
            getattr(self, kind)(el, el.get("part", el.get("name", f"{kind}{n}")))
        kept = [(c, r, p) for (c, r, p) in self.items if self.claims.get((c.x, c.y), -1) <= r]
        return self.split(kept)

    def split(self, kept) -> list[dict]:
        parts: dict[str, list[Component]] = {}
        for c, _, p in kept:
            parts.setdefault(p, []).append(c)
        out = []
        for name, comps in parts.items():
            for sub_name, sub in cut(name, comps):
                xs, ys = [c.x for c in sub], [c.y for c in sub]
                cx, cy = (min(xs) + max(xs)) // 2, (min(ys) + max(ys)) // 2
                local = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in sub]
                local.append(Component(G.CENTRE_MARKER, 0, 0, 0, False))
                out.append({"name": sub_name, "centre": [cx, cy], "comps": local,
                            "bounds": [min(xs), min(ys), max(xs), max(ys)]})
        # every door goes with the part whose bounds hold it (its own part when it was cut)
        for d in self.doors:
            home = [p for p in out if p["name"] == d["part"] or p["name"].startswith(d["part"] + ".")]
            home = [p for p in home if p["bounds"][0] <= d["x"] <= p["bounds"][2]
                    and p["bounds"][1] <= d["y"] <= p["bounds"][3]] or home or out
            p = home[0]
            p.setdefault("doors", []).append({"x": d["x"] - p["centre"][0], "y": d["y"] - p["centre"][1],
                                              "z": d["z"], "facing": d["facing"], "type": d["type"]})
        return out


def cut(name: str, comps: list[Component], limit: int = MAX_COMPONENTS - 64):
    """A part as it is, or cut in two across its longer side until each piece fits a multi."""
    if len(comps) <= limit:
        return [(name, comps)]
    xs, ys = [c.x for c in comps], [c.y for c in comps]
    if max(xs) - min(xs) >= max(ys) - min(ys):
        mid = sorted(xs)[len(xs) // 2]
        a, b = [c for c in comps if c.x < mid], [c for c in comps if c.x >= mid]
    else:
        mid = sorted(ys)[len(ys) // 2]
        a, b = [c for c in comps if c.y < mid], [c for c in comps if c.y >= mid]
    return cut(name + ".a", a, limit) + cut(name + ".b", b, limit)


def walk_tour(stops: list[dict], flights: list[dict]) -> list[dict]:
    """The tour with a stop at each stair's foot and top wherever it changes level: the client's
    pathfinder ignores z, so asked for a cell above it walks to the same x, y below."""
    def dist(a, b):
        return abs(a[0] - b[0]) + abs(a[1] - b[1])

    out = []
    for t in stops:
        if out:
            cur, here, target = out[-1]["z"], (out[-1]["x"], out[-1]["y"]), (t["x"], t["y"])
            for k in range(12):
                up = t["z"] > cur + 4
                if not up and t["z"] >= cur - 4:
                    break
                if up:
                    cands = [f for f in flights if abs(f["z_from"] - cur) <= 4 and f["z_to"] <= t["z"] + 4]
                    key = lambda f: (dist(f["arrive"], target) + dist(f["foot"], here), -f["z_to"])
                else:
                    cands = [f for f in flights if abs(f["z_to"] - cur) <= 4 and f["z_from"] >= t["z"] - 4]
                    key = lambda f: (dist(f["foot"], target) + dist(f["arrive"], here), f["z_from"])
                if not cands:
                    break
                f = min(cands, key=key)
                a, b = (("foot", "z_from"), ("arrive", "z_to")) if up else (("arrive", "z_to"), ("foot", "z_from"))
                for tag, (cell, z) in (("from", (f[a[0]], f[a[1]])), ("to", (f[b[0]], f[b[1]]))):
                    out.append({"name": f"{t['name']}_stair{k}_{tag}", "x": cell[0], "y": cell[1], "z": z})
                cur, here = out[-1]["z"], (out[-1]["x"], out[-1]["y"])
        out.append(t)
    # a stop where the walker already stands is dropped: asked for its own cell, the pathfinder
    # (which ignores z) may walk it to the floor below
    kept = []
    for t in out:
        if kept and (kept[-1]["x"], kept[-1]["y"]) == (t["x"], t["y"]) and abs(kept[-1]["z"] - t["z"]) <= 4:
            if "_stair" in kept[-1]["name"] and "_stair" not in t["name"]:
                kept[-1] = t
            continue
        kept.append(t)
    return kept


def build_scene(desc: dict, cat: Catalogue) -> dict:
    if desc.get("format") != 1 or desc.get("kind") != "scene":
        raise DescriptionError("a scene has format 1 and kind 'scene'")
    s = Scene(desc, cat)
    parts = s.build()
    tour = walk_tour([{"name": t["name"], "x": t["at"][0], "y": t["at"][1], "z": t["z"]}
                      for t in desc.get("tour", [])], s.flights)
    return {"format": 1, "kind": "scene", "name": desc["name"], "parts": parts, "tour": tour, "notes": s.notes}
