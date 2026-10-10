#!/usr/bin/env python3
"""Gaussian-splat LOD (mipmap) decimation for GUO.

A generated PLY (e.g. 64,800 TripoSplat gaussians) is too heavy to draw at all
distances. This builds a LOD chain by opacity/volume-weighted sampling without
replacement: each level keeps the most visually significant gaussians, so a
distant multi draws lod2 (4k) instead of lod0 (65k). The client picks a level
by camera distance (src/Render/Splats/SplatLodChain.cs).

Only binary_little_endian PLYs with a single `element vertex` block are
supported (what SplatToFile3D/SaveGLB write). Property layout is preserved
verbatim; only the vertex count changes.

    python tools/comfy/splatlod.py lod <in.ply> --out-dir build/comfy/multi_lod --ratios 1 0.25 0.0625 0.015625 --seed 7
    python tools/comfy/splatlod.py verify <in.ply> <lod.ply>   # bbox + opacity-mass report
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np

_SCALES = ("scale_0", "scale_1", "scale_2")

# PLY type -> (numpy dtype, size)
_TYPES = {
    "char": ("i1", 1), "uchar": ("u1", 1),
    "short": ("i2", 2), "ushort": ("u2", 2),
    "int": ("i4", 4), "uint": ("u4", 4),
    "float": ("f4", 4), "double": ("f8", 8),
}


def read_ply(path: Path) -> tuple[list[str], list[tuple[str, str]], np.ndarray]:
    """Returns (header_lines, [(name, ply_type)], rows as structured array)."""
    raw = path.read_bytes()
    end = raw.index(b"end_header\n") + len(b"end_header\n")
    header = raw[:end].decode("ascii")
    lines = header.splitlines()
    if lines[0] != "ply" or "binary_little_endian" not in lines[1]:
        raise ValueError(f"{path}: only binary_little_endian PLY supported")
    props: list[tuple[str, str]] = []
    count = 0
    in_vertex = False
    for line in lines[2:]:
        parts = line.split()
        if parts[0] == "element":
            in_vertex = parts[1] == "vertex"
            if in_vertex:
                count = int(parts[2])
        elif parts[0] == "property" and in_vertex:
            if parts[1] == "list":
                raise ValueError(f"{path}: list properties unsupported")
            props.append((parts[2], parts[1]))
    dtype = np.dtype([(n, _TYPES[t][0]) for n, t in props])
    row_size = sum(_TYPES[t][1] for _, t in props)
    buf = raw[end:end + count * row_size]
    if len(buf) != count * row_size:
        raise ValueError(f"{path}: truncated ({len(buf)} of {count * row_size} bytes)")
    return lines, props, np.frombuffer(buf, dtype=dtype, count=count).copy()


def write_ply(path: Path, header_lines: list[str], props, rows: np.ndarray) -> None:
    head = [l for l in header_lines if not l.startswith("element vertex")]
    head.insert(2, f"element vertex {len(rows)}")
    path.write_bytes(("\n".join(head) + "\n").encode("ascii") + rows.tobytes())


def weights(rows: np.ndarray) -> np.ndarray:
    """Visual significance: sigmoid(opacity) * gaussian volume."""
    w = np.ones(len(rows))
    if "opacity" in rows.dtype.names:
        w *= 1.0 / (1.0 + np.exp(-rows["opacity"].astype(np.float64)))
    if all(s in rows.dtype.names for s in _SCALES):
        vol = np.exp(sum(rows[s].astype(np.float64) for s in _SCALES))
        w *= vol / (vol.mean() or 1.0)
    s = w.sum()
    return w / s if s > 0 else np.full(len(rows), 1.0 / len(rows))


def decimate(rows: np.ndarray, keep: int, seed: int) -> np.ndarray:
    if keep >= len(rows):
        return rows.copy()
    rng = np.random.default_rng(seed)
    idx = rng.choice(len(rows), size=keep, replace=False, p=weights(rows))
    idx.sort()  # stable order keeps the file diffable
    return rows[idx].copy()


def lod_chain(src: Path, out_dir: Path, stem: str | None = None,
              ratios: tuple = (1.0, 0.25, 0.0625, 0.015625), seed: int = 7) -> list[Path]:
    header, props, rows = read_ply(src)
    stem = stem or src.stem
    out_dir.mkdir(parents=True, exist_ok=True)
    saved = []
    for level, r in enumerate(ratios):
        keep = max(1, int(round(len(rows) * r)))
        lod = rows if r >= 1.0 else decimate(rows, keep, seed + level)
        dest = out_dir / f"{stem}_lod{level}.ply"
        write_ply(dest, header, props, lod)
        print(f"[splatlod] lod{level}: {len(lod)} gaussians -> {dest} ({dest.stat().st_size} bytes)")
        saved.append(dest)
    return saved


def stats(path: Path) -> dict:
    _, _, rows = read_ply(path)
    xyz = np.stack([rows["x"].astype(np.float64), rows["y"].astype(np.float64),
                    rows["z"].astype(np.float64)])
    mass = float((1.0 / (1.0 + np.exp(-rows["opacity"].astype(np.float64)))).sum()) \
        if "opacity" in rows.dtype.names else float(len(rows))
    return {"count": len(rows), "min": xyz.min(1).tolist(), "max": xyz.max(1).tolist(),
            "opacity_mass": mass}


NODRAW = 0x0001  # generate.py CENTRE_MARKER: invisible, valid, zero visual pollution


def footprint(src: Path, out_dir: Path, name: str, units_per_tile: float = 1.0,
              cover: float = 0.05) -> tuple[Path, Path]:
    """Project a splat to the ground (x,z plane, y-up) and draft its multi footprint.

    Occupied cells (opacity mass >= cover * hottest cell) get invisible nodraw
    markers: exact footprint + positions for MultiEdit to finish with real
    floors, walls and doors. Writes <name>.multi.json (MultiStore components
    description, opens directly in MultiEdit) and <name>.footprint.json (the
    numbers + provenance).
    """
    import json as _json
    _, _, rows = read_ply(src)
    xyz = np.stack([rows["x"].astype(np.float64), rows["y"].astype(np.float64),
                    rows["z"].astype(np.float64)])
    mass = 1.0 / (1.0 + np.exp(-rows["opacity"].astype(np.float64))) \
        if "opacity" in rows.dtype.names else np.ones(len(rows))
    lo = xyz.min(1)
    gx = np.clip(((xyz[0] - lo[0]) / units_per_tile).astype(int), 0, None)
    gz = np.clip(((xyz[2] - lo[2]) / units_per_tile).astype(int), 0, None)
    w, h = int(gx.max()) + 1, int(gz.max()) + 1
    grid = np.zeros((h, w))
    np.add.at(grid, (gz, gx), mass)
    hot = grid.max()
    occ = sorted((x, y) for y in range(h) for x in range(w) if grid[y, x] >= cover * hot)
    y_extent = float(xyz[1].max() - xyz[1].min())
    out_dir.mkdir(parents=True, exist_ok=True)
    parts = [[NODRAW, x, y, 0, 1, 0] for x, y in occ]
    draft = {"format": 1, "kind": "components", "name": name, "source": None,
             "floor_z": 7, "storey_height": 20, "components": parts,
             "guo_footprint": {"from": src.name, "units_per_tile": units_per_tile,
                               "cover": cover, "height_units": round(y_extent, 3),
                               "note": "nodraw markers: replace with floors/walls/doors in MultiEdit"}}
    meta = {"format": 1, "splat": src.name, "size": [w, h], "occupied": len(occ),
            "height_units": round(y_extent, 3), "units_per_tile": units_per_tile,
            "tiles_per_unit": 1.0 / units_per_tile,
            "tool": "splatlod.py footprint",
            "note": "ground projection (x,z), opacity-mass occupancy"}
    draft_path = out_dir / f"{name}.multi.json"
    meta_path = out_dir / f"{name}.footprint.json"
    draft_path.write_text(_json.dumps(draft, indent=1) + "\n", encoding="utf-8")
    meta_path.write_text(_json.dumps(meta, indent=1) + "\n", encoding="utf-8")
    print(f"[splatlod] footprint {name}: {w}x{h}, {len(occ)} cells, height {y_extent:.2f}u -> {draft_path.name}")
    return draft_path, meta_path


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Gaussian-splat LOD decimation")
    sub = ap.add_subparsers(dest="cmd", required=True)
    l = sub.add_parser("lod")
    l.add_argument("src")
    l.add_argument("--out-dir", default="build/comfy/splat_lod")
    l.add_argument("--stem", default=None)
    l.add_argument("--ratios", nargs="+", type=float, default=[1, 0.25, 0.0625, 0.015625])
    l.add_argument("--seed", type=int, default=7)
    v = sub.add_parser("verify")
    v.add_argument("full")
    v.add_argument("lod")
    f = sub.add_parser("footprint")
    f.add_argument("src")
    f.add_argument("--out-dir", default="build/comfy/footprints")
    f.add_argument("--name", required=True)
    f.add_argument("--units-per-tile", type=float, default=1.0)
    f.add_argument("--cover", type=float, default=0.05)
    args = ap.parse_args(argv)
    if args.cmd == "lod":
        lod_chain(Path(args.src), Path(args.out_dir), args.stem,
                  tuple(args.ratios), args.seed)
        return 0
    if args.cmd == "footprint":
        footprint(Path(args.src), Path(args.out_dir), args.name,
                  args.units_per_tile, args.cover)
        return 0
    a, b = stats(Path(args.full)), stats(Path(args.lod))
    print(f"[splatlod] full={a['count']} lod={b['count']} "
          f"mass={a['opacity_mass']:.1f}->{b['opacity_mass']:.1f} "
          f"bbox_preserved={a['min'] == b['min'] and a['max'] == b['max']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
