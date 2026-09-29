"""Raise storeys on buildings that stand in the map's statics, and write the result as a world project.

A description (`kind` "storeys", docs/data_formats.md section 16) names each building by a box of
the map; the building is what stands in it. Its ground storey stays exactly as the map has it
(walls, windows, floor and furnishings, so the shard's doors, vendors and chests stay where they
are). What stands at or above the ground storey's wall top (the old roof, its parapet or thatch)
goes. On top of the ground walls the tool stacks more storeys of a wall family, repeating the
ground storey's outline and partitions (a doorway in the outline becomes wall upstairs, one in a
partition stays open), with windows along the runs, a floor on each, straight stairs where the
description puts them (the house generator's own flights), and a flat roof with a parapet.

The result is whole replaced 8x8 blocks (section 9): the install's land and every other static in
those blocks untouched. tools/world exports and verifies it.
"""
from __future__ import annotations

import datetime
import json
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import generate as G  # noqa: E402
from generate import DescriptionError, signature  # noqa: E402
from guo.uomap import install_fingerprint, open_facet  # noqa: E402
from multifile import Component  # noqa: E402

N4 = ((1, 0), (-1, 0), (0, 1), (0, -1))


def read_area(data_dir: Path, facet: int, x0: int, y0: int, x1: int, y1: int) -> tuple[dict, dict]:
    """Every block that the box touches: {(bx, by): Block}, and {(x, y): [(id, z, hue), ...]} in the box."""
    blocks, cells = {}, {}
    with open_facet(data_dir, facet) as f:
        for bx in range(x0 // 8, x1 // 8 + 1):
            for by in range(y0 // 8, y1 // 8 + 1):
                b = f.read(bx, by)
                blocks[(bx, by)] = b
                for sid, sx, sy, z, hue in b.statics:
                    x, y = bx * 8 + sx, by * 8 + sy
                    if x0 <= x <= x1 and y0 <= y <= y1:
                        cells.setdefault((x, y), []).append((sid, z, hue))
    return blocks, cells


def is_wall(info: dict) -> bool:
    return info.get("role") in ("wall", "window", "post") or "wall" in info.get("flags", [])


# ground walls stand at the base give or take this much: on uneven land the map sets some a unit
# or two off (a wall at 19 in a row at 20)
BASE_SLACK = 2


def footprint(box, cells: dict, pieces: dict, base: int, top: int) -> tuple[set, set]:
    """The building's cells (walls, and what they enclose or its old roof covers) and its ground
    wall cells: pieces standing at the base (within BASE_SLACK) that are walls, windows or posts."""
    walls = {c for c, ss in cells.items()
             if any(abs(z - base) <= BASE_SLACK and is_wall(pieces.get(f"{sid:#06x}", {})) for sid, z, _ in ss)}
    # a flat roof (floor pieces at the wall top) marks the building too; a pitched roof's eaves
    # overhang the walls, so roof pieces do not
    covered = {c for c, ss in cells.items()
               if any(z >= top and pieces.get(f"{sid:#06x}", {}).get("role") != "roof" for sid, z, _ in ss)}
    x0, y0, x1, y1 = box
    inside = (walls | covered) & G.cells_of(box)
    # a doorway (one or two cells between walls in a line) closes the outline for the fill
    for _ in range(2):
        gaps = {(x, y) for x in range(x0, x1 + 1) for y in range(y0, y1 + 1) if (x, y) not in inside and (
            ((x - 1, y) in inside and (x + 1, y) in inside) or ((x, y - 1) in inside and (x, y + 1) in inside) or
            ((x - 1, y) in walls and (x + 2, y) in walls) or ((x, y - 1) in walls and (x, y + 2) in walls))}
        inside |= gaps
    # fill what the outline encloses: flood the box's outside from its rim over cells not in it
    seen, q = set(), deque()
    for x in range(x0 - 1, x1 + 2):
        for y in (y0 - 1, y1 + 1):
            q.append((x, y))
    for y in range(y0 - 1, y1 + 2):
        for x in (x0 - 1, x1 + 1):
            q.append((x, y))
    while q:
        c = q.popleft()
        if c in seen or c in inside or not (x0 - 1 <= c[0] <= x1 + 1 and y0 - 1 <= c[1] <= y1 + 1):
            continue
        seen.add(c)
        q.extend((c[0] + dx, c[1] + dy) for dx, dy in N4)
    fp = {(x, y) for x in range(x0, x1 + 1) for y in range(y0, y1 + 1) if (x, y) not in seen}
    return fp, walls & fp


def raise_building(bd: dict, cat: G.Catalogue, cells: dict) -> tuple[list[Component], set, dict]:
    """New components for one building, the (x, y, z) of the statics it removes, and a record
    (storey z levels, the stairs, the cells each storey stands on)."""
    base = bd.get("base_z", 0)
    step = bd.get("storey_height", 20)
    n = bd["storeys"]
    if n < 1:
        raise DescriptionError(f"{bd['name']}: storeys must be 1 or more (the ground storey is the map's; "
                               "1 only puts a flat roof on it)")
    # the roof: a whole storey up, or `roof_z` lower. The client stands on nothing above z 112 (its
    # pathfinder caps every cell at 128 and a walker needs 16), so a walkable deck is 112 at most
    roof_z = bd.get("roof_z", base + n * step)
    if roof_z + 8 > 127:
        raise DescriptionError(f"{bd['name']}: a roof at {roof_z} leaves no room under z 127")
    if roof_z - (base + (n - 1) * step) < 16:
        raise DescriptionError(f"{bd['name']}: a roof at {roof_z} leaves under 16 of headroom on the top storey")
    top = base + step
    fp, ground_walls = footprint(bd["box"], cells, cat.pieces, base, top)
    if not ground_walls:
        raise DescriptionError(f"{bd['name']}: no walls stand at z {base} in {bd['box']}")
    outline = {(x, y) for (x, y) in fp if any((x + dx, y + dy) not in fp for dx, dy in N4)}
    # upstairs repeats the ground storey's inner walls, or (partitions false) is one hall: a wall
    # round a void below (a hall two storeys tall) has no door to repeat
    partitions = ground_walls - outline if bd.get("partitions", True) else set()
    # a setback: from `storey` up the building steps in `cells` from one side, and the strip left
    # over is a terrace on the storey below (a floor and a parapet): a lower face on a lane
    strip = set()
    sb = bd.get("setback")
    if sb:
        side, deep = sb["side"], sb["cells"]
        axis, far = {"W": (0, False), "E": (0, True), "N": (1, False), "S": (1, True)}[side]
        lines: dict = {}
        for c in fp:
            lines.setdefault(c[1 - axis], []).append(c[axis])
        for other, vals in lines.items():
            edge = max(vals) if far else min(vals)
            for c in fp:
                if c[1 - axis] == other and abs(c[axis] - edge) < deep:
                    strip.add(c)
    set_from = sb.get("storey", 1) if sb else n + 1

    def shape(k):
        """The footprint, its outline and its walls at storey k."""
        f = fp - strip if k >= set_from else fp
        o = {(x, y) for (x, y) in f if any((x + dx, y + dy) not in f for dx, dy in N4)}
        return f, o, o | (partitions & f)
    # what goes: everything in the footprint (and a roof's overhang one cell round it) from the
    # ground storey's wall top up
    grown = {(x + dx, y + dy) for (x, y) in fp for dx in (-1, 0, 1) for dy in (-1, 0, 1)}
    removed = set()
    for c in grown:
        for sid, z, _ in cells.get(c, []):
            info = cat.pieces.get(f"{sid:#06x}", {})
            if z >= top and (c in fp or info.get("role") == "roof"):
                removed.add((c[0], c[1], z, sid))
    b = G.Built()
    wall_mat, floor_mat = bd["wall"], bd.get("floor", "stone")
    # "ground": upstairs repeats the piece the ground storey has in each wall cell (its
    # windows too), so the storeys match; other cells take the ground walls' commonest family
    ground_piece = {}
    if wall_mat == "ground":
        for c in ground_walls:
            ws = [sid for sid, z, _ in cells.get(c, []) if abs(z - base) <= BASE_SLACK and is_wall(cat.pieces.get(f"{sid:#06x}", {}))
                  and cat.pieces[f"{sid:#06x}"].get("height", 0) >= step - 1]
            if ws:
                ground_piece[c] = ws[0]
        mats = [cat.pieces[f"{sid:#06x}"].get("material") for sid in ground_piece.values()]
        wall_mat = max(set(mats), key=mats.count) if mats else "stone"
    every = bd.get("window_every", 3)
    ground_open = fp - ground_walls
    stairs = sorted(bd.get("stairs", []), key=lambda s: s["storey"])
    holes_next: set = set()
    record = {"storeys": [], "stairs": []}
    for k in range(n + 1):                 # k = n is the roof
        z = base + k * step if k < n else roof_z
        if k == 0:
            floor = ground_open
            walls = ground_walls
        else:
            fpk, outline_k, walls = shape(k)
            floor = fpk - holes_next - {c for fh in bd.get("floor_holes", []) if fh["storey"] == k
                                       for c in G.cells_of(fh["box"])}
            ids = cat.floor(floor_mat if k < n else bd.get("roof", {}).get("floor", floor_mat))
            for (x, y) in sorted(floor):
                b.add(ids[(x * 7 + y * 13) % len(ids)], x, y, z)
            if k == set_from and strip:
                # the terrace: the storey below's roof over the strip, with a parapet on its open edges
                t_ids = cat.floor(bd.get("roof", {}).get("floor", floor_mat))
                edge = strip & outline
                for (x, y) in sorted(strip - fpk):
                    b.add(t_ids[(x * 7 + y * 13) % len(t_ids)], x, y, z)
                for (x, y) in sorted(edge):
                    b.add(cat.wall(bd.get("roof", {}).get("parapet", wall_mat), 5, signature(edge, x, y)), x, y, z)
            if k < n:
                for (x, y) in sorted(walls):
                    if (x, y) in ground_piece:
                        b.add(ground_piece[(x, y)], x, y, z)
                        continue
                    sig = signature(walls, x, y)
                    if ground_piece and sig in ("EW", "NS"):
                        # a closed doorway takes the piece of the run it stands in
                        run = ((1, 0), (-1, 0), (2, 0), (-2, 0)) if sig == "EW" else ((0, 1), (0, -1), (0, 2), (0, -2))
                        near = [ground_piece[(x + dx, y + dy)] for dx, dy in run if (x + dx, y + dy) in ground_piece
                                and signature(walls, x + dx, y + dy) == sig]
                        if near:
                            b.add(near[0], x, y, z)
                            continue
                    window = (x, y) in outline_k and sig in ("EW", "NS") and (x + y) % every == 0
                    b.add(cat.wall(wall_mat, step - 1, sig, window=window), x, y, z)
            else:
                para = bd.get("roof", {}).get("parapet", wall_mat)
                for (x, y) in sorted(outline_k):
                    b.add(cat.wall(para, 5, signature(outline_k, x, y)), x, y, z)
                # trim: courses of low wall on the parapet, each `h` high; a corner the family
                # has no piece of that height for takes its lowest course's corner instead
                zt = z + 5
                for h in bd.get("roof", {}).get("trim", []):
                    for (x, y) in sorted(outline_k):
                        sig = signature(outline_k, x, y)
                        sid = cat.wall(para, h, sig)
                        if cat.pieces.get(f"{sid:#06x}", {}).get("material") != cat.pieces.get(
                                f"{cat.wall(para, 5, sig):#06x}", {}).get("material"):
                            sid = cat.wall(para, 2, sig)
                        b.add(sid, x, y, zt)
                    zt += h
        holes_next = set()
        here = [s for s in stairs if s["storey"] == k]
        if here and k >= n:
            raise DescriptionError(f"{bd['name']}: a stair from the roof")
        stand = (floor - walls) if k else ground_open
        for s in here:
            mat = s.get("material", bd.get("stair_material", "stone"))
            rise = (roof_z if k == n - 1 else z + step) - z
            # a step is stood on at half its 5 (the client counts stairs as bridges): n steps reach
            # 5(n - 1) + 2, and the floor above must be within a step (5) of that
            short = {} if rise == G.STAIR_STEPS * 5 else {"steps": max(1, -(-(rise - 7) // 5) + 1), "landing": False}
            h, a = G.staircase(b, cat, s, mat, z, stand, walls if k else ground_walls, **short)
            holes_next |= h
            dx, dy = G.SIDE_STEP[s["rise"]]
            record["stairs"].append({"storey": k, "from_z": z, "to_z": z + rise,
                                     "foot": [s["at"][0] - dx, s["at"][1] - dy], "cells": sorted(h),
                                     "arrive": sorted(a)})
            if k == 0:
                # the ground storey's own pieces on the flight's cells (a table, a carpet edge) go
                for (x, y) in h:
                    for sid, zz, _ in cells.get((x, y), []):
                        if base <= zz < top and not is_wall(cat.pieces.get(f"{sid:#06x}", {})):
                            removed.add((x, y, zz, sid))
        record["storeys"].append({"z": z, "floor": len(floor), "walls": len(walls)})
    hue = int(str(bd.get("hue", "0")), 16) if isinstance(bd.get("hue"), str) else bd.get("hue", 0)
    record.update({"footprint": sorted(fp), "outline": len(outline), "partitions": sorted(partitions),
                   "roof_z": roof_z, "hue": hue})
    return b.comps, removed, record


def build(desc: dict, cat: G.Catalogue, data_dir: Path) -> dict:
    """{"blocks": {(bx, by): {"land": [...], "statics": [...]}}, "buildings": [record], "added": [...]}"""
    if desc.get("format") != 1 or desc.get("kind") != "storeys":
        raise DescriptionError("format must be 1 and kind 'storeys'")
    cat.fresh()
    facet = desc.get("facet", 0)
    # scenes: a new build from the scene generator (fort.py) put down whole at a site, in place
    # of what stood in its clear boxes (a building rebuilt from nothing on its own plot)
    import fort
    placed, scene_problems = [], []
    for ns in desc.get("scenes", []):
        sc = fort.build_scene(ns["scene"], cat)
        scene_problems += [f"{ns['scene']['name']}: {q}" for q in sc.get("problems", []) if "overlap" not in q]
        ox, oy, oz = ns["at"][0], ns["at"][1], ns.get("z", 0)
        hue = int(str(ns.get("hue", "0")), 16) if isinstance(ns.get("hue"), str) else ns.get("hue", 0)
        pcs = [(c.item, c.x + p["centre"][0] + ox, c.y + p["centre"][1] + oy, c.z + oz, hue)
               for p in sc["parts"] for c in p["comps"] if c.visible]
        placed.append((ns, pcs))
    boxes = ([bd["box"] for bd in desc.get("buildings", [])] + [pv["box"] for pv in desc.get("paving", [])]
             + [rs["box"] for rs in desc.get("resurface", []) + desc.get("reclad", [])]
             + [r["box"] for r in desc.get("reland", []) + desc.get("strip", [])]
             + [[pr["at"][0], pr["at"][1], pr["at"][0], pr["at"][1]] for pr in desc.get("props", []) + desc.get("remove", []) + desc.get("land", [])]
             + [bx for ns, _ in placed for bx in ns.get("clear", [])]
             + [[min(p[1] for p in pcs), min(p[2] for p in pcs), max(p[1] for p in pcs), max(p[2] for p in pcs)]
                for _, pcs in placed if pcs])
    xs = [v for bx in boxes for v in (bx[0], bx[2])]
    ys = [v for bx in boxes for v in (bx[1], bx[3])]
    blocks, cells = read_area(data_dir, facet, min(xs) - 1, min(ys) - 1, max(xs) + 1, max(ys) + 1)
    added: list[tuple[int, int, int, int, int]] = []
    removed: set = set()
    records = []
    for ns, pcs in placed:
        for (x, y) in (c for bx in ns.get("clear", []) for c in G.cells_of(bx)):
            removed |= {(x, y, z, sid) for sid, z, _ in cells.get((x, y), [])}
        added += pcs
    # props: loose pieces at map cells (street dressing, room decor, rooftop kit), kept as given
    added += [(int(str(pr["item"]), 16), pr["at"][0], pr["at"][1], pr["z"], int(str(pr.get("hue", "0")), 16))
              for pr in desc.get("props", [])]
    for bd in desc.get("buildings", []):
        comps, gone, rec = raise_building(bd, cat, cells)
        rec["name"] = bd["name"]
        records.append(rec)
        removed |= gone
        added += [(c.item, c.x, c.y, c.z, rec["hue"]) for c in comps if c.visible]
    # swaps: one piece put in place of the wall at a cell and height (an arch in a wall); "new"
    # allows a cell with no wall there (a doorway the piece closes)
    for sw in desc.get("swaps", []):
        x, y, z, item = sw["at"][0], sw["at"][1], sw["z"], int(str(sw["item"]), 16)
        walls_here = [(sid, zz) for sid, zz, _ in cells.get((x, y), []) if zz == z and is_wall(cat.pieces.get(f"{sid:#06x}", {}))]
        removed |= {(x, y, zz, sid) for sid, zz in walls_here}
        before = len(added)
        added = [a for a in added if not (a[1] == x and a[2] == y and a[3] == z and is_wall(cat.pieces.get(f"{a[0]:#06x}", {})))]
        if before == len(added) and not walls_here and not sw.get("new"):
            raise DescriptionError(f"swap at {x},{y} z {z}: no wall there to replace")
        added.append((item, x, y, z, 0))
    # paving: floor pieces at the land's height on flat cells (all four corners level) that hold
    # no statics, e.g. a square in front of a door
    for pv in desc.get("paving", []):
        ids = cat.floor(pv["floor"], pv.get("variants", 4))
        land = {}
        x0, y0, x1, y1 = pv["box"]
        for (bx, by), blk in blocks.items():
            for i in range(64):
                land[(bx * 8 + i % 8, by * 8 + i // 8)] = blk.land_z[i]
        taken = {(a[1], a[2]) for a in added}
        for (x, y) in G.cells_of(pv["box"]):
            z = land.get((x, y))
            if z is None or cells.get((x, y)) or (x, y) in taken:
                continue
            if any(land.get((x + dx, y + dy), z) != z for dx, dy in ((1, 0), (0, 1), (1, 1))):
                continue
            added.append((ids[(x * 7 + y * 13) % len(ids)], x, y, z, 0))
    # resurface: the map's own deck or floor pieces of the listed ids in the box, each put back
    # as a piece of another floor family at the same cell and z (a timber dock laid in stone)
    for rs in desc.get("resurface", []):
        ids = cat.floor(rs["floor"], rs.get("variants", 4))
        old = {int(str(i), 16) for i in rs["from"]}
        n = 0
        for (x, y) in G.cells_of(rs["box"]):
            for sid, z, _ in cells.get((x, y), []):
                if sid in old and (x, y, z, sid) not in removed:
                    removed.add((x, y, z, sid))
                    added.append((ids[(x * 7 + y * 13) % len(ids)], x, y, z, 0))
                    n += 1
        if not n:
            raise DescriptionError(f"resurface {rs['box']}: none of {rs['from']} there")
    def ids_of(spec):
        out_ = set()
        for v in spec:
            lo, _, hi = str(v).partition("-")
            out_ |= set(range(int(lo, 16), int(hi or lo, 16) + 1))
        return out_
    # reclad: the map's own wall, window, post and stair pieces of one material in the box, each
    # put back as the piece of another material with the same part, height and joins (a timber
    # shed clad in brick); a piece the other material has no match for stays and is counted
    for rc in desc.get("reclad", []):
        src, dst = cat.material(rc["from"]), cat.material(rc["to"])
        kinds = rc.get("kinds", ["wall", "window", "post", "stair"])
        only = ids_of(rc["ids"]) if rc.get("ids") else None
        where = {}
        for kind in kinds:
            for key, v in src.get(kind, {}).items():
                for sig, ids in (v.items() if isinstance(v, dict) else [("", v)]):
                    for i in ids:
                        if only is None or int(i, 16) in only:
                            where.setdefault(int(i, 16), (kind, key, sig))
        def match(kind, key, sig):
            got = dst.get(kind, {})
            if key in got:
                v = got[key]
                if not isinstance(v, dict):
                    return v
                if sig in v:
                    return v[sig]
            if kind == "stair" or not key.isdigit():
                return None
            # the nearest height, then the closest joins (the most shared sides, the fewest extra);
            # a window or post the other material lacks becomes its plain wall
            want = set(sig) - {"-"}
            best = None
            for k2 in (kind, "wall") if kind != "wall" else ("wall",):
                for h, v in dst.get(k2, {}).items():
                    if not (h.isdigit() and isinstance(v, dict)):
                        continue
                    for s2, ids2 in v.items():
                        have = set(s2) - {"-"}
                        score = (k2 != kind, abs(int(h) - int(key)), -len(want & have), len(have - want))
                        if best is None or score < best[0]:
                            best = (score, ids2)
            return best[1] if best else None
        n = kept = 0
        for (x, y) in G.cells_of(rc["box"]):
            for sid, z, hue in cells.get((x, y), []):
                if sid not in where or (x, y, z, sid) in removed:
                    continue
                ids = match(*where[sid])
                if not ids:
                    kept += 1
                    continue
                removed.add((x, y, z, sid))
                added.append((int(ids[0], 16), x, y, z, hue))
                n += 1
        if not n:
            raise DescriptionError(f"reclad {rc['box']}: no {rc['from']} pieces there")
        if kept:
            scene_problems.append(f"reclad {rc['box']}: {kept} {rc['from']} piece(s) have no {rc['to']} match and stay")
    # reland: land cells in the box whose id is listed get one of `to` (grass laid as paving),
    # except in the `keep` boxes (a park); strip: statics whose tiledata name holds one of `names`
    # go (the trees and bushes on it)
    # remove: exact map statics to take out (loose furniture a prop replaces), each {item, at, z}
    for rm in desc.get("remove", []):
        sid, (x, y), z = int(str(rm["item"]), 16), rm["at"], rm["z"]
        if any(s == sid and zz == z for s, zz, _ in cells.get((x, y), [])):
            removed.add((x, y, z, sid))
    relanded = set()
    for rl in desc.get("reland", []):
        frm, to, keep = ids_of(rl["from"]), sorted(ids_of(rl["to"])), rl.get("keep", [])
        x0, y0, x1, y1 = rl["box"]
        # sparse N: only a cell with fewer than N listed cells in the 5 x 5 round it (a stray
        # patch goes, a road or a yard of them stays)
        sparse = rl.get("sparse")
        if sparse:
            listed = {(bx * 8 + i % 8, by * 8 + i // 8) for (bx, by), blk in blocks.items() for i in range(64)
                      if blk.land_id[i] in frm}
        for (bx, by), blk in blocks.items():
            for i in range(64):
                x, y = bx * 8 + i % 8, by * 8 + i // 8
                if x0 <= x <= x1 and y0 <= y <= y1 and blk.land_id[i] in frm and not any(
                        k[0] <= x <= k[2] and k[1] <= y <= k[3] for k in keep) and not (sparse and sum(
                        (x + dx, y + dy) in listed for dx in range(-2, 3) for dy in range(-2, 3)) - 1 >= sparse):
                    blk.land_id[i] = to[(x * 7 + y * 13) % len(to)]
                    relanded.add((bx, by))
        # level N: a paved cell (one of `to`) with nothing standing on it, lying within N of the
        # middle height of its eight neighbours, takes that height: a lone dip or bump shades
        # the four tiles round it into a dark cross once the ground is one flat material
        if rl.get("level"):
            tos, lz = set(to), {}
            for (bx, by), blk in blocks.items():
                for i in range(64):
                    lz[(bx * 8 + i % 8, by * 8 + i // 8)] = (blk, i)
            new = {}
            for (x, y), (blk, i) in lz.items():
                if not (x0 <= x <= x1 and y0 <= y <= y1) or blk.land_id[i] not in tos or cells.get((x, y)):
                    continue
                nb = sorted(lz[(x + dx, y + dy)][0].land_z[lz[(x + dx, y + dy)][1]]
                            for dx in (-1, 0, 1) for dy in (-1, 0, 1) if (dx or dy) and (x + dx, y + dy) in lz)
                if len(nb) == 8:
                    mid = (nb[3] + nb[4]) // 2
                    if mid != blk.land_z[i] and abs(mid - blk.land_z[i]) <= rl["level"]:
                        new[(x, y)] = mid
            for (x, y), z in new.items():
                blk, i = lz[(x, y)]
                blk.land_z[i] = z
                relanded.add((x // 8, y // 8))
    # land: single cells painted by hand, {at, id, z?}: the tile (and height) of that cell
    if desc.get("land"):
        at_cell = {(bx * 8 + i % 8, by * 8 + i // 8): (blk, i) for (bx, by), blk in blocks.items() for i in range(64)}
        for ld in desc["land"]:
            x, y = ld["at"]
            if (x, y) not in at_cell:
                continue
            blk, i = at_cell[(x, y)]
            blk.land_id[i] = int(str(ld["id"]), 16)
            if "z" in ld:
                blk.land_z[i] = ld["z"]
            relanded.add((x // 8, y // 8))
    if desc.get("strip"):
        from guo.uoread import TileData
        td = TileData(data_dir)
        for sp in desc["strip"]:
            names, keep = [n.lower() for n in sp["names"]], sp.get("keep", [])
            for (x, y) in G.cells_of(sp["box"]):
                if any(k[0] <= x <= k[2] and k[1] <= y <= k[3] for k in keep):
                    continue
                for sid, z, _ in cells.get((x, y), []):
                    if any(n in (td.static(sid) or {}).get("name", "").lower() for n in names):
                        removed.add((x, y, z, sid))
    out = {}
    for (bx, by), blk in blocks.items():
        statics = []
        for sid, sx, sy, z, hue in blk.statics:
            if (bx * 8 + sx, by * 8 + sy, z, sid) not in removed:
                statics.append((sid, sx, sy, z, hue))
        mine = [(sid, x - bx * 8, y - by * 8, z, hue) for sid, x, y, z, hue in added if x // 8 == bx and y // 8 == by]
        if not mine and len(statics) == len(blk.statics) and (bx, by) not in relanded:
            continue
        out[(bx, by)] = {"land": blk, "statics": statics + mine}
    bad = [c for c in added if not -128 <= c[3] <= 127]
    if bad:
        raise DescriptionError(f"{len(bad)} piece(s) outside the map's z range -128..127, e.g. {bad[0]}")
    return {"facet": facet, "blocks": out, "buildings": records, "added": added, "removed": sorted(removed),
            "problems": scene_problems}


def write_project(built: dict, folder: Path, name: str, cfg) -> list[Path]:
    """A world project (section 9) holding the replaced blocks."""
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "project.json").write_text(json.dumps({
        "format": 1, "name": name, "created": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "base": {"client_version": cfg.client_version, "fingerprint": install_fingerprint(cfg.client_data)}},
        indent=1) + "\n", encoding="utf-8")
    bdir = folder / "blocks" / str(built["facet"])
    bdir.mkdir(parents=True, exist_ok=True)
    written = []
    for (bx, by), v in sorted(built["blocks"].items()):
        blk = v["land"]
        land = [" ".join(f"{blk.land_id[y * 8 + x]:04X}:{blk.land_z[y * 8 + x]}" for x in range(8)) for y in range(8)]
        statics = [{"id": f"0x{sid:04X}", "x": sx, "y": sy, "z": z, "hue": f"0x{hue:04X}"}
                   for sid, sx, sy, z, hue in sorted(v["statics"], key=lambda s: (s[2], s[1], s[3], s[0]))]
        p = bdir / f"{bx}_{by}.json"
        p.write_text(json.dumps({"format": 1, "facet": built["facet"], "block": [bx, by], "land": land,
                                 "statics": statics}, indent=1) + "\n", encoding="utf-8")
        written.append(p)
    # the project is this build's alone: a block an earlier build wrote and this one does not
    # (a reland taken out) goes, or it would outlive the description that made it
    for old in bdir.glob("*.json"):
        if old not in written:
            old.unlink()
    return written
