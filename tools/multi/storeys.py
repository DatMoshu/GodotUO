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


def footprint(box, cells: dict, pieces: dict, base: int, top: int) -> tuple[set, set]:
    """The building's cells (walls, and what they enclose or its old roof covers) and its ground
    wall cells: pieces standing at the base that are walls, windows or posts."""
    walls = {c for c, ss in cells.items()
             if any(z == base and is_wall(pieces.get(f"{sid:#06x}", {})) for sid, z, _ in ss)}
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
    if n < 2:
        raise DescriptionError(f"{bd['name']}: storeys must be 2 or more (the ground storey is the map's)")
    if base + n * step + 8 > 127:
        raise DescriptionError(f"{bd['name']}: a roof at {base + n * step} leaves no room under z 127")
    top = base + step
    fp, ground_walls = footprint(bd["box"], cells, cat.pieces, base, top)
    if not ground_walls:
        raise DescriptionError(f"{bd['name']}: no walls stand at z {base} in {bd['box']}")
    outline = {(x, y) for (x, y) in fp if any((x + dx, y + dy) not in fp for dx, dy in N4)}
    partitions = ground_walls - outline
    upper_walls = outline | partitions
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
    every = bd.get("window_every", 3)
    ground_open = fp - ground_walls
    stairs = sorted(bd.get("stairs", []), key=lambda s: s["storey"])
    holes_next: set = set()
    record = {"storeys": [], "stairs": []}
    for k in range(n + 1):                 # k = n is the roof
        z = base + k * step
        if k == 0:
            floor = ground_open
            walls = ground_walls
        else:
            walls = upper_walls
            floor = fp - holes_next
            ids = cat.floor(floor_mat if k < n else bd.get("roof", {}).get("floor", floor_mat))
            for (x, y) in sorted(floor):
                b.add(ids[(x * 7 + y * 13) % len(ids)], x, y, z)
            if k < n:
                for (x, y) in sorted(walls):
                    sig = signature(walls, x, y)
                    window = (x, y) in outline and sig in ("EW", "NS") and (x + y) % every == 0
                    b.add(cat.wall(wall_mat, step - 1, sig, window=window), x, y, z)
            else:
                para = bd.get("roof", {}).get("parapet", wall_mat)
                for (x, y) in sorted(outline):
                    b.add(cat.wall(para, 5, signature(outline, x, y)), x, y, z)
        holes_next = set()
        here = [s for s in stairs if s["storey"] == k]
        if here and k >= n:
            raise DescriptionError(f"{bd['name']}: a stair from the roof")
        stand = (floor - walls) if k else ground_open
        for s in here:
            mat = s.get("material", bd.get("stair_material", "stone"))
            h, a = G.staircase(b, cat, s, mat, z, stand, walls if k else ground_walls)
            holes_next |= h
            dx, dy = G.SIDE_STEP[s["rise"]]
            record["stairs"].append({"storey": k, "from_z": z, "to_z": z + step,
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
                   "roof_z": base + n * step, "hue": hue})
    return b.comps, removed, record


def build(desc: dict, cat: G.Catalogue, data_dir: Path) -> dict:
    """{"blocks": {(bx, by): {"land": [...], "statics": [...]}}, "buildings": [record], "added": [...]}"""
    if desc.get("format") != 1 or desc.get("kind") != "storeys":
        raise DescriptionError("format must be 1 and kind 'storeys'")
    cat.fresh()
    facet = desc.get("facet", 0)
    xs = [v for bd in desc["buildings"] for v in (bd["box"][0], bd["box"][2])]
    ys = [v for bd in desc["buildings"] for v in (bd["box"][1], bd["box"][3])]
    blocks, cells = read_area(data_dir, facet, min(xs) - 1, min(ys) - 1, max(xs) + 1, max(ys) + 1)
    added: list[tuple[int, int, int, int, int]] = []
    removed: set = set()
    records = []
    for bd in desc["buildings"]:
        comps, gone, rec = raise_building(bd, cat, cells)
        rec["name"] = bd["name"]
        records.append(rec)
        removed |= gone
        added += [(c.item, c.x, c.y, c.z, rec["hue"]) for c in comps if c.visible]
    out = {}
    for (bx, by), blk in blocks.items():
        statics = []
        for sid, sx, sy, z, hue in blk.statics:
            if (bx * 8 + sx, by * 8 + sy, z, sid) not in removed:
                statics.append((sid, sx, sy, z, hue))
        mine = [(sid, x - bx * 8, y - by * 8, z, hue) for sid, x, y, z, hue in added if x // 8 == bx and y // 8 == by]
        if not mine and len(statics) == len(blk.statics):
            continue
        out[(bx, by)] = {"land": blk, "statics": statics + mine}
    return {"facet": facet, "blocks": out, "buildings": records, "added": added, "removed": sorted(removed)}


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
    return written
