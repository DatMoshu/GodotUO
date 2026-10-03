"""Generators over a style: autowall, roof, stairs and house. JSON in, JSON out (run.py subcommands).

Every generator is deterministic: the same parameters and seed give the same bytes. Each takes a
`cat` (styles.StyleCatalogue) and a style key as `style`. The result is a dict:

  components  [[item, x, y, z] or [item, x, y, z, 0]]  (0: hidden, as the shard fills it)
  doors       [{x, y, z, storey, facing, type, closed, open}]
  notes       what a style lacked and how the generator went on without it
  problems    what the validator refused (house only)
  ms          how long the generation took

autowall, roof and stairs give coordinates as asked (the editor places them); house centres its result
like the client's own houses, on (W // 2, H // 2).
"""
from __future__ import annotations

import random
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import generate  # noqa: E402
from generate import DescriptionError, Built, bbox, cells_of, edge, floor_of, region, signature  # noqa: E402
from multifile import Component  # noqa: E402

DIRS4 = {"N": (0, -1), "E": (1, 0), "S": (0, 1), "W": (-1, 0)}


def comps_out(comps: list[Component]) -> list[list]:
    return [c.as_list() for c in comps]


def zof(cat, mat: str, name: str) -> int:
    return cat.z(mat, name)


def ms_since(t0: float) -> float:
    return round((time.perf_counter() - t0) * 1000, 2)


# --- paths -------------------------------------------------------------------------------------

def line_cells(a, b) -> list[tuple[int, int]]:
    """Cells from a to b, 4-connected: a diagonal becomes a staircase."""
    (x, y), (bx, by) = a, b
    out = [(x, y)]
    sx, sy = (bx > x) - (bx < x), (by > y) - (by < y)
    dx, dy = abs(bx - x), abs(by - y)
    err = 0
    while (x, y) != (bx, by):
        if dx >= dy and x != bx:
            x += sx
            err += dy
            out.append((x, y))
            if err * 2 >= dx and y != by:
                y += sy
                err -= dx
                out.append((x, y))
        elif y != by:
            y += sy
            out.append((x, y))
        else:
            x += sx
            out.append((x, y))
    return out


def expand_path(path: list, closed: bool = False) -> tuple[list[tuple[int, int]], list[list[tuple[int, int]]]]:
    """(cells in order without repeats, runs): a run is a maximal straight stretch of cells."""
    pts = [tuple(p) for p in path]
    if closed and pts and pts[0] != pts[-1]:
        pts.append(pts[0])
    cells: list = []
    for a, b in zip(pts, pts[1:]):
        seg = line_cells(a, b)
        cells += seg if not cells else seg[1:]
    if len(pts) == 1:
        cells = [pts[0]]
    runs, cur = [], []
    for c in cells:
        if len(cur) >= 2:
            d0 = (cur[1][0] - cur[0][0], cur[1][1] - cur[0][1])
            if (c[0] - cur[-1][0], c[1] - cur[-1][1]) != d0:
                runs.append(cur)
                cur = [cur[-1]]
        cur.append(c)
    if cur:
        runs.append(cur)
    return cells, runs


# --- autowall ----------------------------------------------------------------------------------

def autowall(cat, p: dict) -> dict:
    """A wall along a path: corner and junction pieces by neighbours, a window every N cells of a run,
    a door. {style, path, closed, z, storeys, window_every, window_offset, windows, door, existing}"""
    t0 = time.perf_counter()
    mat = p["style"]
    cat.notes.clear()
    cells, runs = expand_path(p["path"], p.get("closed", False))
    walls = set(cells) | {tuple(c) for c in p.get("existing", [])}
    z0 = p.get("z", zof(cat, mat, "floor"))
    wall_h = p.get("height", zof(cat, mat, "wall"))
    step_h = zof(cat, mat, "storey")
    storeys = max(1, int(p.get("storeys", 1)))
    every = int(p.get("window_every", 3))
    off = int(p.get("window_offset", every // 2))
    door_spec = p.get("door", {"index": "middle"})
    if door_spec is True:
        door_spec = {"index": "middle"}
    door_cell = None
    if door_spec:
        if "at" in door_spec:
            door_cell = tuple(door_spec["at"])
        else:
            longest = max(runs, key=len) if runs else []
            idx = door_spec.get("index", "middle")
            if idx == "middle":
                door_cell = longest[len(longest) // 2] if len(longest) > 2 else None
            elif 0 <= int(idx) < len(cells):
                door_cell = cells[int(idx)]
        if door_cell is not None and door_cell not in walls:
            raise DescriptionError(f"the door {list(door_cell)} is not on the wall")
    skip = {tuple(q) for q in p.get("no_window", [])}
    windows: set = set()
    if p.get("windows", True) and every > 0:
        for run in runs:
            for i, c in enumerate(run):
                if 0 < i < len(run) - 1 and i % every == off % every and c != door_cell and \
                        len(signature(walls, *c)) == 2 and c not in skip:
                    windows.add(c)
    if door_cell:
        # no window straight beside a door: it would hang in the door's frame
        windows -= {c for c in windows if abs(c[0] - door_cell[0]) + abs(c[1] - door_cell[1]) <= 1}
    b = Built()
    doors: list = []
    kind = (door_spec or {}).get("kind", "wood")
    for k in range(storeys):
        z = z0 + k * step_h
        for c in sorted(set(cells)):
            if c == door_cell and k == (door_spec or {}).get("storey", 0):
                along_x = (c[0] - 1, c[1]) in walls or (c[0] + 1, c[1]) in walls
                closed, opened = cat.door_pair(mat, "EW" if along_x else "NS", kind)
                b.add(closed, c[0], c[1], z, visible=bool(p.get("visible_doors", False)))
                doors.append({"x": c[0], "y": c[1], "z": z, "storey": k, "facing": "WestCW" if along_x else "SouthCW",
                              "type": cat.door_type(mat, kind), "closed": closed, "open": opened})
                continue
            b.add(cat.wall(mat, wall_h, signature(walls, *c), window=c in windows), c[0], c[1], z)
    return {"components": comps_out(b.comps), "doors": doors, "notes": list(dict.fromkeys(cat.notes)),
            "cells": [list(c) for c in cells], "windows": sorted(map(list, windows)), "ms": ms_since(t0)}


# --- roofs -------------------------------------------------------------------------------------

def hip_cells(cat, mat: str, box, top: int, overhang: int = 1) -> list[tuple[int, int, int, int]]:
    """A hip roof over a wall box: rings that shrink one cell a course, 3 z apart, over x0+1..x1+overhang
    (the gable roof's overhang): edge slopes N/S/W/E, corner pieces where the style has them, a ridge where
    one dimension runs out, a cap where both do."""
    x0, y0, x1, y1 = box
    xl, xh, yl, yh = x0 + 1, x1 + overhang, y0 + 1, y1 + overhang
    step = zof(cat, mat, "roof_step")
    out = []
    k = 0

    def piece(side, fallback):
        try:
            return cat.roof(mat, side)
        except (DescriptionError, KeyError):
            return cat.roof(mat, fallback)
    while xl <= xh and yl <= yh:
        z = top + step * k
        if xl == xh and yl == yh:
            out.append((piece("cap", "ridge_y"), xl, yl, z))
        elif xl == xh:
            out += [(cat.roof(mat, "ridge_y"), xl, y, z) for y in range(yl, yh + 1)]
        elif yl == yh:
            out += [(cat.roof(mat, "ridge_x"), x, yl, z) for x in range(xl, xh + 1)]
        else:
            for x in range(xl, xh + 1):
                out.append((piece("NW" if x == xl else "NE", "N") if x in (xl, xh) else cat.roof(mat, "N"), x, yl, z))
                out.append((piece("SW" if x == xl else "SE", "S") if x in (xl, xh) else cat.roof(mat, "S"), x, yh, z))
            for y in range(yl + 1, yh):
                out.append((cat.roof(mat, "W"), xl, y, z))
                out.append((cat.roof(mat, "E"), xh, y, z))
        xl, xh, yl, yh, k = xl + 1, xh - 1, yl + 1, yh - 1, k + 1
    return out


def fit_gable(box, ridge: str | None) -> tuple[tuple, str, bool]:
    """The box with its dimension across the ridge made even (the ridge sits on one tile), and the ridge
    axis ('y' along the longer depth). Returns (box, ridge, widened)."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    ridge = ridge if ridge in ("x", "y") else ("y" if h >= w else "x")
    across = w if ridge == "y" else h
    if across % 2:
        return ((x0, y0, x1 + 1, y1) if ridge == "y" else (x0, y0, x1, y1 + 1)), ridge, True
    return tuple(box), ridge, False


def roof(cat, p: dict) -> dict:
    """{style, kind gable|hip|flat, boxes (wall boxes [x0,y0,x1,y1] or {box, z, ridge}), z (the roof's base),
    ridge, parapet, fill, overhang}. Where roofs overlap the higher wins. A gable with an odd span is widened."""
    t0 = time.perf_counter()
    mat = p["style"]
    cat.notes.clear()
    kind = p.get("kind", "gable")
    top0 = p.get("z", zof(cat, mat, "floor") + zof(cat, mat, "storey"))
    boxes = [(b if isinstance(b, dict) else {"box": b}) for b in p.get("boxes", [p["box"]] if "box" in p else [])]
    if not boxes:
        raise DescriptionError("roof: give box or boxes")
    mats = {"roof": mat, "wall": mat, "floor": mat}
    by_cell: dict = {}
    for i, bx in enumerate(boxes):
        box = tuple(bx["box"])
        top = bx.get("z", top0)
        rb = Built()
        if kind == "gable":
            box2, ridge, widened = fit_gable(box, bx.get("ridge", p.get("ridge")))
            if widened:
                cat.notes.append(f"roof box {list(box)}: widened to {list(box2)} (a gable's span across the ridge must be odd)")
            generate.roof_gable(rb, cat, {"ridge": ridge, "material": mat}, mats, box2[2] - box2[0], box2[3] - box2[1], top, box2[0], box2[1])
            if not p.get("fill", True):
                rb.comps = [c for c in rb.comps if cat.pieces.get(f"{c.item:#06x}", {}).get("role") not in ("wall", "post")]
        elif kind == "hip":
            for item, x, y, z in hip_cells(cat, mat, box, top, p.get("overhang", 1)):
                rb.add(item, x, y, z)
        elif kind == "flat":
            r = {"style": "flat", "material": mat}
            if p.get("parapet"):
                r["parapet"] = mat
            generate.roof_rect(rb, cat, r, mats, box, top)
        else:
            raise DescriptionError(f"roof kind '{kind}': gable, hip or flat")
        for c in rb.comps:
            by_cell.setdefault((c.x, c.y), {}).setdefault(i, []).append(c)
    comps = []
    for _cell, by_box in sorted(by_cell.items()):
        comps += max(by_box.values(), key=lambda cs: max(c.z for c in cs))
    return {"components": comps_out(comps), "doors": [], "notes": list(dict.fromkeys(cat.notes)), "ms": ms_since(t0)}


# --- stairs ------------------------------------------------------------------------------------

def turn(d: tuple[int, int], side: str) -> tuple[int, int]:
    """The direction to the left or right of walking along d (x east, y south)."""
    return (d[1], -d[0]) if side == "left" else (-d[1], d[0])


def stair_cells(kind: str, at, rise: str, width: int = 1, steps: int = 4, side: str = "left") -> dict:
    """The footprint of a flight, without pieces. rows: [(cells, level, direction, is_step)], the last row
    is where a climber arrives (on the floor above); holes: every row before it; foot: the cell before."""
    d = DIRS4[rise]
    ax, ay = at

    def row(cx, cy, dd):
        across = (1, 0) if dd[0] == 0 else (0, 1)
        return [(cx + k * across[0], cy + k * across[1]) for k in range(width)]
    seq: list = []
    if kind == "turned":
        d2 = turn(d, side)
        first = steps // 2
        seq += [(row(ax + i * d[0], ay + i * d[1], d), i, d, True) for i in range(first)]
        px, py = ax + first * d[0], ay + first * d[1]                        # the mid landing
        seq.append((row(px, py, d), first, d, False))
        for j in range(1, steps - first + 1):
            seq.append((row(px + j * d2[0], py + j * d2[1], d2), first + j - 1, d2, True))
        last = steps - first
        for j in (last + 1, last + 2):                                       # the top landing, then the arrival
            seq.append((row(px + j * d2[0], py + j * d2[1], d2), steps, d2, False))
    else:
        for i in range(steps + 2):
            seq.append((row(ax + i * d[0], ay + i * d[1], d), i, d, i < steps))
    return {"rows": seq, "holes": [c for r in seq[:-1] for c in r[0]], "arrive": seq[-1][0],
            "foot": (ax - d[0], ay - d[1])}


def stairs(cat, p: dict) -> dict:
    """{style, at [x,y], rise N|E|S|W, kind straight|turned|ladder, turn left|right, width, steps, z,
    open (a box or cells the flight may stand on), above {box, z, floor}}. The result lists the cells the
    floor above must leave open (`holes`) and where a climber arrives; with `above` that floor is laid
    with the hole cut."""
    t0 = time.perf_counter()
    mat = p["style"]
    cat.notes.clear()
    kind = p.get("kind", "straight")
    rise = p.get("rise", "S")
    z = p.get("z", zof(cat, mat, "floor"))
    width = int(p.get("width", 1))
    steps = int(p.get("steps", 4))
    at = tuple(p["at"])
    b = Built()
    geo = None
    if kind == "ladder":
        lad = cat.ladder(mat)
        if lad:
            d = DIRS4[rise]
            b.add(lad, at[0], at[1], z)
            geo = {"holes": [at], "arrive": [(at[0] + d[0], at[1] + d[1])], "foot": (at[0] - d[0], at[1] - d[1])}
            cat.notes.append("a ladder is art only: the shard must carry a climber (a teleporter) to the arrival")
        else:
            cat.notes.append(f"style '{mat}' has no ladder: a straight flight is built")
            kind = "straight"
    if geo is None:
        geo = stair_cells(kind, at, rise, width, steps, p.get("turn", "left"))
        allow = p.get("open")
        if isinstance(allow, list) and len(allow) == 4 and isinstance(allow[0], int):
            allow = cells_of(tuple(allow))
        elif allow is not None:
            allow = {tuple(c) for c in allow}
        for cells, _i, _d, _s in geo["rows"]:
            for c in cells:
                if allow is not None and c not in allow:
                    raise DescriptionError(f"stair: cell {list(c)} is not open floor")
        for cells, i, d, is_step in geo["rows"][:-1]:                         # the last row is on the floor above
            rowset = set(cells)
            asc = next(k for k, v in DIRS4.items() if v == d)
            for (x, y) in cells:
                if is_step:
                    piece = cat.step(mat, asc, signature(rowset, x, y))
                    block = cat.block(piece)
                    for k in range(i):
                        b.add(block, x, y, z + 5 * k)
                    b.add(piece, x, y, z + 5 * i)
                else:                                   # a landing: blocks up to its level
                    block = cat.block(cat.step(mat, asc, ""))
                    for k in range(i):
                        b.add(block, x, y, z + 5 * k)
    holes = sorted(map(tuple, geo["holes"]))
    comps = list(b.comps)
    if p.get("above"):
        a = p["above"]
        zf = a.get("z", z + zof(cat, mat, "storey"))
        ids = cat.floor(a.get("floor", mat))
        for (x, y) in sorted(cells_of(tuple(a["box"])) - set(holes)):
            comps.append(Component(generate.scatter(ids, x, y), x, y, zf))
    return {"components": comps_out(comps), "doors": [], "notes": list(dict.fromkeys(cat.notes)),
            "holes": [list(c) for c in holes], "arrive": [list(c) for c in geo["arrive"]],
            "foot": list(geo["foot"]), "kind": kind, "ms": ms_since(t0)}


# --- houses ------------------------------------------------------------------------------------

SHAPES = ("rect", "L", "T", "U", "cross")


def wings_for(shape: str, w: int, d: int) -> tuple[list[tuple], list[str]]:
    """The wall boxes of a footprint: wings whose union is the plan. A shape needs about 10 x 10."""
    notes = []
    w, d = max(8, w), max(8, d)
    if shape != "rect" and (w < 10 or d < 10):
        notes.append(f"{shape} needs at least 10 x 10: a rectangle is built")
        shape = "rect"

    def ev(n):
        return n - (n % 2)
    if shape == "rect":
        return [(0, 0, w, d)], notes
    if shape == "L":
        da = max(6, ev(round(d * 0.55)))
        wb = max(6, ev(round(w * 0.5)))
        return [(0, 0, w, da), (0, da, wb, d)], notes
    if shape == "T":
        db = max(6, ev(round(d * 0.45)))
        sw = max(6, ev(round(w * 0.5)))
        sx = (w - sw) // 2
        return [(0, 0, w, db), (sx, db, sx + sw, d)], notes
    if shape == "U":
        aw = max(4, ev(round(w * 0.3)))
        db = max(6, ev(round(d * 0.45)))
        return [(0, 0, aw, d), (w - aw, 0, w, d), (0, d - db, w, d)], notes
    t = max(6, ev(round(min(w, d) * 0.5)))                                 # cross: two bars
    cy, cx = (d - t) // 2, (w - t) // 2
    return [(0, cy, w, cy + t), (cx, 0, cx + t, d)], notes


def split_rooms(box, target: int, walls: set, reserved: set, rng: random.Random, min_side: int = 3):
    """Recursive room split of a wall box by partitions: the largest room is cut along its longer axis at
    a random line, until `target` rooms. Returns (partitions [{x|y, from, to}], doorways [(x, y)], rooms)."""
    rooms = [tuple(box)]
    parts, doors = [], []
    walls = set(walls)
    tries = 0
    while len(rooms) < target and tries < 24:
        tries += 1
        rooms.sort(key=lambda r: -((r[2] - r[0]) * (r[3] - r[1])))
        x0, y0, x1, y1 = rooms[0]
        by_x = (x1 - x0) >= (y1 - y0)
        lo, hi = ((x0, x1) if by_x else (y0, y1))
        cands = list(range(lo + min_side, hi - min_side + 1))
        rng.shuffle(cands)
        done = False
        for k in cands:
            line = [(k, y) for y in range(y0, y1 + 1)] if by_x else [(x, k) for x in range(x0, x1 + 1)]
            if set(line) & reserved or line[0] not in walls or line[-1] not in walls or {line[0], line[-1]} & set(doors):
                continue
            spots = []
            for c in line[2:-2]:
                side = ((c[0] + 1, c[1]), (c[0] - 1, c[1])) if by_x else ((c[0], c[1] + 1), (c[0], c[1] - 1))
                if not set(side) & walls and not {(c[0] + dx, c[1] + dy) for dx, dy in ((0, 1), (1, 0), (0, -1), (-1, 0))} & reserved:
                    spots.append(c)
            if not spots:
                continue
            door = spots[rng.randrange(len(spots))]
            parts.append({"x": k, "from": y0, "to": y1} if by_x else {"y": k, "from": x0, "to": x1})
            doors.append(door)
            walls |= set(line)
            rooms[0:1] = [(x0, y0, k, y1), (k, y0, x1, y1)] if by_x else [(x0, y0, x1, k), (x0, k, x1, y1)]
            done = True
            break
        if not done:
            rooms.append(rooms.pop(0))
    return parts, doors, rooms


def house(cat, p: dict) -> dict:
    """{style, seed, shape rect|L|T|U|cross, width, depth, storeys 1-3, rooms, roof gable|hip|flat|none,
    porch, balcony, windows, window_every, foundation, vary_wings, door, yard}: a house description from the
    seed, expanded by generate.build, then checked by validate."""
    import validate
    t0 = time.perf_counter()
    desc, notes = house_description(cat, p)
    comps, side = generate.build(desc, cat)
    problems = validate.validate(comps, side, None)
    return {"components": comps_out(comps), "doors": side["doors"],
            "notes": list(dict.fromkeys(notes + side["notes"] + cat.notes)), "problems": problems,
            "description": desc, "size": side["size"], "centre": side["centre"], "storeys": side["storeys"],
            "stops": side["stops"], "ms": ms_since(t0)}


def house_description(cat, p: dict) -> tuple[dict, list[str]]:
    mat = p["style"]
    cat.notes.clear()
    notes: list[str] = []
    seed = int(p.get("seed", 1))
    rng = random.Random(seed)
    shape = p.get("shape", "rect")
    if shape not in SHAPES:
        raise DescriptionError(f"shape '{shape}': {', '.join(SHAPES)}")
    n_storeys = max(1, min(3, int(p.get("storeys", 1))))
    roof_kind = p.get("roof", "gable")
    wing_boxes, wn = wings_for(shape, int(p.get("width", 12)), int(p.get("depth", 10)))
    notes += wn
    boxes = [fit_gable(bx, None)[0] if roof_kind == "gable" else tuple(bx) for bx in wing_boxes]
    big = max(range(len(boxes)), key=lambda i: (boxes[i][2] - boxes[i][0]) * (boxes[i][3] - boxes[i][1]))
    rects = []
    for i, bx in enumerate(boxes):
        st = n_storeys if i == big or not p.get("vary_wings", True) else rng.randint(1, n_storeys)
        r = {"box": list(bx), "storeys": st}
        if roof_kind == "gable":
            r["roof"] = {"style": "gable", "ridge": fit_gable(bx, None)[1]}
        elif roof_kind in ("hip", "flat"):
            r["roof"] = {"style": roof_kind}
        rects.append(r)
    top_n = max(r["storeys"] for r in rects)

    def footprint(n):
        return region(tuple(r["box"]) for r in rects if r["storeys"] > n)
    ground = footprint(0)
    gx0, gy0, gx1, gy1 = bbox(ground)
    mb = tuple(rects[big]["box"])
    # the front door: the southmost straight wall cell, near the middle of the main wing
    ring = edge(ground)
    front_y = max(y for _, y in ring)
    row = sorted(x for x, y in ring if y == front_y and (x - 1, y) in ring and (x + 1, y) in ring)
    if not row:
        raise DescriptionError("no straight south wall for a door")
    mid = (mb[0] + mb[2]) / 2 if mb[3] == front_y else (row[0] + row[-1]) / 2
    door_x = min(row, key=lambda x: (abs(x - mid), x))
    door_cell = (door_x, front_y)
    reserved = [{(door_x + dx, front_y - 1 - dy) for dx in (-1, 0, 1) for dy in (0, 1)} for _ in range(top_n)]
    # stairs first, in the main wing, so partitions keep clear of them
    stair_specs: list = []
    for n in range(top_n - 1):
        fp_n = floor_of(footprint(n))
        fp_up = floor_of(footprint(n + 1))
        walls_n = edge(footprint(n))
        spots = []
        for rise in "ESWN":
            for x in range(mb[0] + 1, mb[2]):
                for y in range(mb[1] + 1, mb[3]):
                    geo = stair_cells("straight", (x, y), rise)
                    cells = [c for r_ in geo["rows"] for c in r_[0]]
                    foot = geo["foot"]
                    if not (all(c in fp_n and c in fp_up and c not in walls_n for c in cells) and foot in fp_n
                            and foot not in walls_n):
                        continue
                    hug = any((c[0] + dx, c[1] + dy) in walls_n for c in cells for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
                    near = any(abs(c[0] - door_x) + abs(c[1] - front_y) <= 2 for c in cells + [foot])
                    used = any(set(cells) & set(s["cells"]) for s in stair_specs)
                    if hug and not near and not used:
                        spots.append((x, y, rise, cells, foot))
        if not spots:
            notes.append(f"no room for a stair from storey {n} to {n + 1}: place one by hand")
            continue
        x, y, rise, cells, foot = spots[rng.randrange(len(spots))]
        stair_specs.append({"storey": n, "at": [x, y], "rise": rise, "cells": cells, "foot": foot})
        zone = {(c[0] + dx, c[1] + dy) for c in cells + [foot] for dx in (-1, 0, 1) for dy in (-1, 0, 1)}
        for k in (n, n + 1):
            if k < top_n:
                reserved[k] |= zone
    storeys = []
    n_rooms = max(1, int(p.get("rooms", 3)))
    every = int(p.get("window_every", 3))
    for n in range(top_n):
        outer = edge(footprint(n))
        walls = set(outer)
        openings, parts = [], []
        if n == 0:
            openings.append({"kind": "door", "at": list(door_cell), "door": p.get("door", "wood")})
        if n_rooms > 1:
            parts, dcells, rooms = split_rooms(mb, n_rooms, walls, reserved[n], rng)
            if len(rooms) < n_rooms:
                notes.append(f"storey {n}: {len(rooms)} rooms of {n_rooms} fit")
            for pt in parts:
                walls |= ({(pt["x"], y) for y in range(pt["from"], pt["to"] + 1)} if "x" in pt else
                          {(x, pt["y"]) for x in range(pt["from"], pt["to"] + 1)})
            openings += [{"kind": "door", "at": list(c), "door": p.get("interior_door", p.get("door", "wood"))} for c in dcells]
        if p.get("windows", True) and every > 0:
            used = {tuple(o["at"]) for o in openings}
            for (x, y) in sorted(outer):
                sig = signature(walls, x, y)
                if sig not in ("EW", "NS") or (x, y) in used:
                    continue
                along = x if sig == "EW" else y
                if along % every == every // 2 and not any(abs(x - u[0]) + abs(y - u[1]) <= 1 for u in used):
                    openings.append({"kind": "window", "at": [x, y]})
        st = {"openings": sorted(openings, key=lambda o: (o["kind"], o["at"]))}
        if parts:
            st["partitions"] = parts
        flights = [{"at": s["at"], "rise": s["rise"]} for s in stair_specs if s["storey"] == n]
        if flights:
            st["stairs"] = flights
        storeys.append(st)
    mats = {"wall": mat, "floor": mat, "steps": mat, "roof": mat, "stairs": mat}
    if cat.style(mat).get("foundation") and p.get("foundation", True):
        mats["foundation"] = mat
    if cat.style(mat).get("doors"):
        mats["door"] = p.get("door", "wood")
    desc = {"format": 1, "name": p.get("name", f"house_{seed}"), "rects": rects, "materials": mats, "storeys": storeys,
            "floor_z": zof(cat, mat, "floor"), "storey_height": zof(cat, mat, "storey"),
            "wall_height": zof(cat, mat, "wall")}
    if cat.style(mat).get("windows"):
        cat.set_window(mat, rng.randrange(len(cat.style(mat)["windows"])))
    if p.get("porch", True):
        pw = 2 if gx1 - gx0 >= 8 else 1
        px0, px1 = door_x - pw, door_x + pw
        if all((x, front_y) in ring for x in range(px0, px1 + 1)):
            porch = {"box": [px0, front_y, px1, front_y + 3], "floor": mat, "posts": mat,
                     "entry": {"side": "S", "offset": door_x - px0}}
            if p.get("balcony") and top_n > 1:
                porch["balcony"] = {"rail": mat, "rail_height": 5}
            desc["porches"] = [porch]
        else:
            notes.append("the south wall is too short for a porch")
    if p.get("yard"):
        y = p["yard"] if isinstance(p["yard"], dict) else {}
        m = 3
        desc["yard"] = {"box": [gx0 - m, gy0 - m, gx1 + m, gy1 + m + (3 if "porches" in desc else 0)],
                        "fence": y.get("style", mat), "height": y.get("height", 11),
                        "gate": {"side": "S", "offset": door_x - (gx0 - m)}, "path": y.get("path", mat)}
    return desc, notes
