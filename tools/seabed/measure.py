"""Measure how a facet's shallows are built: depth by shore distance, what lies on the bed and how it groups.

    python tools/seabed/run.py measure [--data DIR] [--facet 0] [--override FILE] [--band 24] [--out FILE]

Reads any client data folder (the configured install by default), read only. --override takes a
files_override.txt (as tools/seabed writes) so a staged seabed is measured with the same ruler as
the map it is compared to. Prints a summary and, with --out, writes the numbers as JSON.

What is measured, per shore distance d (Chebyshev cells from the nearest dry cell):
  - how the water is made there: opaque wet land, a dug bed under a water static, or neither;
  - the depth of a dug bed (surface z minus land z) and the land ids used for it;
  - how many cells hold a submerged object, and of which kind (tiledata name);
and over the whole band: how objects group (cluster sizes, kinds per cluster, spacing between
clusters) and how much of the shallows has nothing on it.

The output is aggregate numbers only: no cell of the measured map is copied anywhere.
"""
from __future__ import annotations

import json
from collections import Counter
from pathlib import Path

import numpy as np

WET = 0x80
TRANSLUCENT = 0x8
NO_Z = -128


def _paths(data: Path, facet: int, override: Path | None):
    from guo import uomap
    land, idx, sta = uomap.facet_files(data, facet)
    tiledata = None
    if override:
        for line in override.read_text(encoding="utf-8").splitlines():
            name, _, path = line.partition("=")
            name = name.strip().lower()
            if not path:
                continue
            if name == f"staidx{facet}.mul":
                idx = Path(path)
            elif name == f"statics{facet}.mul":
                sta = Path(path)
            elif name in (f"map{facet}.mul", f"map{facet}legacymul.uop"):
                land = Path(path)
            elif name == "tiledata.mul":
                tiledata = Path(path).parent
    return land, idx, sta, tiledata or data


def scan(data: Path, facet: int, override: Path | None = None):
    """Arrays [x, y] of land id and z, and one row per static: x, y, id, z, hue."""
    from guo import uomap, uoread
    land_p, idx_p, sta_p, td_dir = _paths(Path(data), facet, override)
    td = uoread.TileData(td_dir)
    with uomap.Facet(land_p, idx_p, sta_p, facet) as f:
        wb, hb = f.width_blocks, f.height_blocks
        W, H = wb * 8, hb * 8
        land = np.zeros((W, H), np.uint16)
        lz = np.zeros((W, H), np.int8)
        for bx in range(wb):
            for by in range(hb):
                raw = np.frombuffer(f.map, np.uint8, 192, f.map_offset(f.number(bx, by)) + 4).reshape(64, 3)
                land[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8] = (raw[:, 0].astype(np.uint16) | (raw[:, 1].astype(np.uint16) << 8)).reshape(8, 8).T
                lz[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8] = raw[:, 2].view(np.int8).reshape(8, 8).T
        n = wb * hb
        idx = np.frombuffer(f.idx, np.int32, n * 3).reshape(n, 3).copy()
        rec = np.dtype([("id", "<u2"), ("x", "u1"), ("y", "u1"), ("z", "i1"), ("hue", "<u2")])
        sta_len = len(f.sta)
        parts = []
        ok = (idx[:, 0] >= 0) & (idx[:, 1] > 0) & (idx[:, 0].astype(np.int64) + idx[:, 1] <= sta_len)
        for b in np.nonzero(ok)[0].tolist():
            off, length = int(idx[b, 0]), int(idx[b, 1]) // 7 * 7
            r = np.frombuffer(f.sta, rec, length // 7, off)
            bx, by = divmod(b, hb)
            parts.append((r["x"].astype(np.int32) + bx * 8, r["y"].astype(np.int32) + by * 8, r["id"].copy(), r["z"].copy(), r["hue"].copy()))
        if parts:
            sx, sy, sid, sz, hue = (np.concatenate([p[k] for p in parts]) for k in range(5))
        else:
            sx = sy = np.zeros(0, np.int32); sid = hue = np.zeros(0, np.uint16); sz = np.zeros(0, np.int8)
        keep = (sid != 0) & (sid != 0xFFFF) & (sx < W) & (sy < H)
        raw = r = None   # views of the mapped files: released before the facet closes
    return td, land, lz, (sx[keep], sy[keep], sid[keep], sz[keep], hue[keep])


def _flags(td, count, getter):
    out = np.zeros(count, np.uint64)
    for i in range(count):
        e = getter(i)
        out[i] = e["flags"] if e else 0
    return out


def measure(data: Path, facet: int = 0, override: Path | None = None, band: int = 24) -> dict:
    from scipy import ndimage
    td, land, lz, (sx, sy, sid, sz, hue) = scan(data, facet, override)
    W, H = land.shape
    land_flags = _flags(td, 0x4000, td.land)
    nstatic = min(td.static_count, 0x10000)
    static_flags = _flags(td, nstatic, td.static)
    static_height = np.array([(td.static(i) or {"height": 0})["height"] for i in range(nstatic)], np.int16)
    known = sid < nstatic
    sx, sy, sid, sz, hue = sx[known], sy[known], sid[known], sz[known], hue[known]

    wet_land = (land_flags[land] & WET) != 0
    s_wet = (static_flags[sid] & WET) != 0
    surface = np.full((W, H), NO_Z, np.int16)
    np.maximum.at(surface, (sx[s_wet], sy[s_wet]), sz[s_wet].astype(np.int16))
    wet_static = surface > NO_Z
    water = wet_land | wet_static
    surface = np.where(wet_static, surface, lz.astype(np.int16))
    dist = ndimage.distance_transform_cdt(water, metric="chessboard")
    bed = wet_static & ~wet_land
    depth = surface - lz.astype(np.int16)

    # Objects: a non-wet static on a water cell, with its top no higher than 4 over the surface
    # (bed plants and rocks, wrecks; not piers, ships and bridges standing over the water).
    obj = ~s_wet & water[sx, sy]
    top = sz.astype(np.int16) + static_height[sid]
    under = obj & (top <= surface[sx, sy] + 4)
    over = obj & ~under
    ox, oy, oid, oz, ohue = sx[under], sy[under], sid[under], sz[under], hue[under]
    has = np.zeros((W, H), bool)
    has[ox, oy] = True

    def name(i):
        return (td.static(int(i)) or {"name": "?"})["name"].strip().lower() or "(unnamed)"

    def pct(a, qs=(10, 50, 90, 99)):
        return {f"p{q}": round(float(np.percentile(a, q)), 1) for q in qs} if a.size else {}

    rows = []
    for d in range(1, band + 1):
        at = dist == d
        n = int(at.sum())
        if not n:
            continue
        b = at & bed
        dv = depth[b]
        ids = Counter(land[b].tolist()).most_common(4)
        rows.append({
            "d": d, "cells": n,
            "opaque_wet_land": round(float((at & wet_land & ~wet_static).sum()) / n, 4),
            "wet_land_under_static": round(float((at & wet_land & wet_static).sum()) / n, 4),
            "dug_bed": round(float(b.sum()) / n, 4),
            "bed_depth_median": float(np.median(dv)) if dv.size else None,
            "bed_depth_p10_p90": [float(np.percentile(dv, 10)), float(np.percentile(dv, 90))] if dv.size else None,
            "bed_land_top": [[f"0x{i:X}", (td.land(i) or {'name': '?'})['name'], c] for i, c in ids],
            "object_cells": round(float((at & has).sum()) / n, 4),
        })

    in_band = (dist >= 1) & (dist <= band)
    kinds = Counter(name(i) for i in oid[in_band[ox, oy]].tolist())
    ids_top = Counter(oid[in_band[ox, oy]].tolist()).most_common(40)
    hues = Counter(ohue[in_band[ox, oy]].tolist()).most_common(8)

    # What lies where: the kinds in each distance bucket, and how high over the bed land they stand.
    od = dist[ox, oy]
    buckets = []
    for lo, hi in ((1, 2), (3, 5), (6, 10), (11, 16), (17, band)):
        pick = (od >= lo) & (od <= hi)
        c = Counter(name(i) for i in oid[pick].tolist())
        total = max(1, sum(c.values()))
        buckets.append({"d": [lo, hi], "objects": int(pick.sum()), "per_cell": round(float(pick.sum()) / max(1, int(((dist >= lo) & (dist <= hi)).sum())), 4),
                        "kinds": [[k, round(n / total, 3)] for k, n in c.most_common(8)]})
    over_bed = (oz.astype(np.int16) - lz[ox, oy].astype(np.int16))[in_band[ox, oy] & bed[ox, oy]]
    stacked = np.zeros((W, H), np.uint16)
    np.add.at(stacked, (ox[in_band[ox, oy]], oy[in_band[ox, oy]]), 1)

    # Groups: object cells joined when they touch or have one empty cell between them.
    occupied = has & in_band
    joined = ndimage.binary_dilation(occupied, structure=np.ones((3, 3), bool))
    labels, count = ndimage.label(joined, structure=np.ones((3, 3), bool))
    lab = labels[ox, oy]
    sel = (lab > 0) & in_band[ox, oy]
    cells_per = np.bincount(labels[occupied], minlength=count + 1)[1:]
    cells_per = cells_per[cells_per > 0]
    pairs = np.unique(np.stack([lab[sel].astype(np.int64), oid[sel].astype(np.int64)], 1), axis=0)
    kinds_per = np.bincount(pairs[:, 0], minlength=count + 1)[1:]
    kinds_per = kinds_per[kinds_per > 0]

    # Spacing: from each water cell of the band with nothing on it, how far to the nearest object.
    far = ndimage.distance_transform_cdt(~occupied, metric="chessboard")
    empty = in_band & ~occupied
    chunks = lambda m: m[: W // 8 * 8, : H // 8 * 8].reshape(W // 8, 8, H // 8, 8).any(axis=(1, 3))
    shallow_chunks = chunks(in_band & bed)
    water_ids = Counter(sid[s_wet].tolist()).most_common(12)
    return {
        "facet": facet, "width": W, "height": H, "band": band,
        "water_cells": int(water.sum()), "band_cells": int(in_band.sum()),
        "dug_bed_cells": int(bed.sum()), "dug_bed_in_band": round(float((bed & in_band).sum()) / max(1, int(in_band.sum())), 4),
        "bed_reach": {**pct(dist[bed]), "max": int(dist[bed].max()) if bed.any() else 0},
        "profile": rows,
        "objects_in_band": int(in_band[ox, oy].sum()), "objects_over_water": int(over.sum()),
        "object_kinds": kinds.most_common(25),
        "object_ids": [[f"0x{i:X}", name(i), c] for i, c in ids_top],
        "object_hues": hues,
        "objects_by_distance": buckets,
        "object_z_over_bed": pct(over_bed) if over_bed.size else {},
        "objects_per_occupied_cell": pct(stacked[stacked > 0]) if (stacked > 0).any() else {},
        "groups": int(cells_per.size), "group_cells": pct(cells_per), "group_single_cell_share": round(float((cells_per == 1).mean()), 4) if cells_per.size else None,
        "group_distinct_ids": pct(kinds_per),
        "empty_cell_distance_to_object": pct(far[empty]) if empty.any() else {},
        "bed_chunks": int(shallow_chunks.sum()),
        "bed_chunks_with_objects": int((shallow_chunks & chunks(occupied)).sum()),
        "water_statics": [[f"0x{i:X}", name(i), c, bool(int(static_flags[i]) & TRANSLUCENT)] for i, c in water_ids],
    }


def summary(m: dict) -> str:
    out = [f"facet {m['facet']} {m['width']}x{m['height']}: water {m['water_cells']:,} cells, band 1..{m['band']} {m['band_cells']:,}, "
           f"dug bed {m['dug_bed_cells']:,} ({m['dug_bed_in_band']:.0%} of the band), bed reach {m['bed_reach']}"]
    out.append("  d   cells  opaque  dug-bed  depth(med, p10..p90)  objects  bed land")
    for r in m["profile"]:
        dep = f"{r['bed_depth_median']:.0f} {r['bed_depth_p10_p90'][0]:.0f}..{r['bed_depth_p10_p90'][1]:.0f}" if r["bed_depth_median"] is not None else "-"
        top = ", ".join(f"{i} {n} {c}" for i, n, c in r["bed_land_top"][:2])
        out.append(f" {r['d']:2d} {r['cells']:7d}  {r['opaque_wet_land']:6.1%}  {r['dug_bed']:6.1%}  {dep:>18}  {r['object_cells']:6.2%}  {top}")
    out.append(f"objects in band {m['objects_in_band']:,} (over water, left out: {m['objects_over_water']:,}); groups {m['groups']:,}; "
               f"cells per group {m['group_cells']}; single-cell groups {m['group_single_cell_share']}; ids per group {m['group_distinct_ids']}")
    out.append(f"empty band cell to nearest object {m['empty_cell_distance_to_object']}; bed chunks {m['bed_chunks']:,}, with objects {m['bed_chunks_with_objects']:,}")
    for b in m["objects_by_distance"]:
        out.append(f"  d {b['d'][0]}..{b['d'][1]}: {b['per_cell']:.3f} objects a cell: " + ", ".join(f"{k} {v:.0%}" for k, v in b["kinds"]))
    out.append(f"object z over the bed land {m['object_z_over_bed']}; objects on an occupied cell {m['objects_per_occupied_cell']}")
    out.append("kinds: " + ", ".join(f"{k} {c}" for k, c in m["object_kinds"][:18]))
    out.append("hues: " + ", ".join(f"{h} x{c}" for h, c in m["object_hues"]))
    out.append("water statics: " + ", ".join(f"{i} {c}{' translucent' if t else ''}" for i, _n, c, t in m["water_statics"]))
    return "\n".join(out)


def run(data: Path, facet: int, override: Path | None, band: int, out: Path | None) -> int:
    m = measure(data, facet, override, band)
    print(summary(m))
    if out:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(m, indent=1) + "\n", encoding="utf-8")
        print(f"[seabed] measurements in {out}")
    return 0

