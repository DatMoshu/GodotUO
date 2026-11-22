"""Seabed layout for a UO facet: pure planning plus the statics writer.

The shallows of Felucca are land tiles dug below the water statics (bed
0x4C-0x64 at z -15, water at z -5), and nothing lives on them. This module
reads that, defines which water is shallow, and plans sea plants, corals,
rocks and sand patches on it, in groups, thinning out toward the deep.

Nothing here writes to the install: the writer copies statics/staidx into a
stage folder and refuses a stage inside the install.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

WET_LAND = (0xA8, 0xA9, 0xAA, 0xAB, 0x136, 0x137)
WATER_STATIC = (0x1796, 0x17B2)
BED_FIRST, BED_LAST = 0x4C, 0x64
NO_STATIC = -127

# Client art (tiledata names in the comments). Sources in tools/seabed/README.md.
SAND_RIPPLE = (0x2468, 0x2469, 0x246A, 0x246B)          # "sand" diamonds with ripples
SAND_RIPPLE_B = (0x245E, 0x245F, 0x2460, 0x2461, 0x2462, 0x2463)
ROCKS_SMALL = (0x1363, 0x1364, 0x1365, 0x1368, 0x1369, 0x136B, 0x136C, 0x136D)  # "rock"/"rocks"
ROCKS_CLUSTER = (0x1367, 0x136A)                        # "rocks": 2-3 stones
BOULDERS = (0x1771, 0x1772, 0x1773, 0x1774, 0x1775, 0x1776)  # large brown/grey "rock"
PLANTS = (0xD04, 0xD05)                                 # "water plants", "reeds"
CORAL = (0x4C2A, 0x4C2B, 0x4C2C, 0x4C2D, 0x4C2E, 0x4C2F, 0x4C30, 0x4C31)  # "Sea Coral"/"Sea Sponge"
CORAL_B = (0xA38B, 0xA38D, 0xA38E, 0xA38F, 0xA386)      # fire/staghorn/plate/red coral, anemone
SHELLS = (0xFC4, 0xFC5, 0xFC6, 0xFC8, 0xFC9, 0xFCA, 0xFCB, 0xFCC)

SHALLOW_MAX = 12  # cells from the nearest dry cell (Chebyshev) that still count as shallow
DENSITY_CAP = 0.5


@dataclass
class Plan:
    """What the generator decided: statics as (x, y, id, z) in facet cells."""
    placed: list
    water_cells: int
    shallow_cells: int
    water_blocks: int
    eligible_blocks: int
    covered_blocks: int


def _hash2(x, y, seed):
    """Deterministic per-cell 0..1 value (vectorised integer hash)."""
    h = (x.astype(np.uint64) * np.uint64(0x9E3779B1) + y.astype(np.uint64) * np.uint64(0x85EBCA77)
         + np.uint64(seed) * np.uint64(0xC2B2AE3D))
    h ^= h >> np.uint64(15)
    h *= np.uint64(0x2C1B3C6D)
    h ^= h >> np.uint64(12)
    h *= np.uint64(0x297A2D39)
    h ^= h >> np.uint64(15)
    return (h & np.uint64(0xFFFFFF)).astype(np.float64) / float(0x1000000)


def _value_noise(w, h, scale, seed):
    """Smooth 0..1 noise with feature size `scale` cells (bilinear over a hashed lattice)."""
    gx = np.arange(w // scale + 3)
    gy = np.arange(h // scale + 3)
    lat = _hash2(gx[:, None] * np.ones((1, len(gy)), np.int64), np.ones((len(gx), 1), np.int64) * gy[None, :], seed)
    xs = np.arange(w) / scale
    ys = np.arange(h) / scale
    x0 = xs.astype(int); y0 = ys.astype(int)
    fx = xs - x0; fy = ys - y0
    fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy)
    a = lat[x0][:, y0]; b = lat[x0 + 1][:, y0]; c = lat[x0][:, y0 + 1]; d = lat[x0 + 1][:, y0 + 1]
    fxx = fx[:, None]; fyy = fy[None, :]
    return (a * (1 - fxx) + b * fxx) * (1 - fyy) + (c * (1 - fxx) + d * fxx) * fyy


def water_masks(land, land_z, wet_z):
    """(water, shallow_bed): water is any wet land or wet static; the bed is a cell
    whose land lies at least 5 below its water static (a dug seabed)."""
    wet_land = np.isin(land, WET_LAND)
    wet_static = wet_z > NO_STATIC
    water = wet_land | wet_static
    bed = wet_static & ~wet_land & (land_z.astype(np.int16) <= wet_z.astype(np.int16) - 5)
    return water, bed


def shore_distance(water):
    """Chebyshev distance of each water cell to the nearest dry cell (0 on land)."""
    from scipy.ndimage import distance_transform_cdt
    return distance_transform_cdt(water, metric="chessboard")


def plan(land, land_z, wet_z, occupied, seed=1, origin=(0, 0), shallow_max=SHALLOW_MAX):
    """Plan seabed objects. Arrays are [x, y]. `occupied` marks cells that already
    hold a non-water static (piers, boats, ruins) and stay untouched.

    Shallow rule: a dug seabed cell (see water_masks) within `shallow_max` cells of dry land.
    Bands by shore distance d:  rim d<=2 rocks and sand; shelf 3..6 plants, corals, sand;
    slope 7+ boulder fields, coral heads, sparse weed. Groups come from two noise fields
    (a plant meadow field and a rock field), so objects clump and leave clear sand between.
    """
    water, bed = water_masks(land, land_z, wet_z)
    w, h = land.shape
    dist = shore_distance(water)
    shallow = bed & (dist <= shallow_max) & ~occupied
    xs = np.arange(w)[:, None] * np.ones((1, h), np.int64) + origin[0]
    ys = np.ones((w, 1), np.int64) * np.arange(h)[None, :] + origin[1]
    r = _hash2(xs, ys, seed)
    r2 = _hash2(xs, ys, seed + 7)
    meadow = _value_noise(w, h, 9, seed + 1)
    rocky = _value_noise(w, h, 7, seed + 2)
    sandy = _value_noise(w, h, 11, seed + 3)

    kind = np.zeros((w, h), np.uint8)   # 0 none 1 sand 2 rock 3 boulder 4 plant 5 coral 6 shell
    rim = dist <= 2
    shelf = (dist >= 3) & (dist <= 6)
    slope = dist >= 7
    cand = shallow
    kind[cand & (sandy > 0.80)] = 1   # whole patches, not a sprinkle
    rock_zone = cand & (rocky > 0.6)
    kind[rock_zone & (r2 < 0.30) & (rim | shelf)] = 2
    kind[cand & (sandy > 0.80) & (rocky <= 0.6) & (r2 > 0.9)] = 6
    kind[rock_zone & (r2 < 0.12) & slope] = 3
    kind[cand & rim & (rocky > 0.72) & (r2 < 0.08)] = 3
    plant_zone = cand & (meadow > 0.45) & (kind == 0)
    kind[plant_zone & shelf & (r < 0.45)] = 4
    kind[plant_zone & slope & (r < 0.18)] = 4
    kind[plant_zone & rim & (r < 0.14)] = 4
    coral_zone = cand & (meadow < 0.34) & (kind == 0) & (dist >= 4)
    kind[coral_zone & (r2 < 0.14)] = 5
    kind[cand & (kind == 0) & (r < 0.012)] = 6

    placed = []
    for k in range(1, 7):
        ix, iy = np.nonzero(kind == k)
        for x, y in zip(ix.tolist(), iy.tolist()):
            rr = _pick_unit(x + origin[0], y + origin[1], seed, k)
            placed.append((x, y, _pick_id(k, rr, int(dist[x, y])), int(land_z[x, y])))

    def blocks(mask):
        return int(mask[: w // 8 * 8, : h // 8 * 8].reshape(w // 8, 8, h // 8, 8).any(axis=(1, 3)).sum())
    cov = np.zeros((w, h), bool)
    for x, y, *_ in placed:
        cov[x, y] = True
    return Plan(placed, int(water.sum()), int(shallow.sum()), blocks(water), blocks(bed & (dist <= shallow_max)), blocks(cov))


def _pick_unit(x, y, seed, k):
    v = _hash2(np.array([x]), np.array([y]), seed * 31 + k * 101)[0]
    return float(v)


def _pick_id(k, u, d):
    def pick(seq):
        return seq[min(int(u * len(seq)), len(seq) - 1)]
    if k == 1:
        return pick(SAND_RIPPLE if u < 0.7 else SAND_RIPPLE_B)
    if k == 2:
        return pick(ROCKS_SMALL if u < 0.7 else ROCKS_CLUSTER)
    if k == 3:
        return pick(BOULDERS)
    if k == 4:
        return PLANTS[0] if u < 0.8 else PLANTS[1]
    if k == 5:
        return pick(CORAL if u < 0.7 else CORAL_B)
    return pick(SHELLS)


# ---------------------------------------------------------------- reading a facet

def scan_facet(facet):
    """Read a uomap.Facet into [x, y] arrays: land id, land z, wet static z, and
    cells holding any other (non-water) static."""
    W, H = facet.width_blocks * 8, facet.height_blocks * 8
    land = np.zeros((W, H), np.uint16)
    lz = np.zeros((W, H), np.int8)
    wet = np.full((W, H), NO_STATIC, np.int8)
    occ = np.zeros((W, H), bool)
    for bx in range(facet.width_blocks):
        for by in range(facet.height_blocks):
            n = facet.number(bx, by)
            raw = np.frombuffer(facet.map, dtype=np.uint8, count=192, offset=facet.map_offset(n) + 4).reshape(64, 3)
            ids = raw[:, 0].astype(np.uint16) | (raw[:, 1].astype(np.uint16) << 8)
            land[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8] = ids.reshape(8, 8).T
            lz[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8] = raw[:, 2].view(np.int8).reshape(8, 8).T
            off, length = facet.statics_span(n)
            if not length:
                continue
            r = np.frombuffer(facet.sta, dtype=np.uint8, count=length // 7 * 7, offset=off).reshape(-1, 7)
            for k in range(len(r)):
                sid = int(r[k, 0]) | (int(r[k, 1]) << 8)
                if sid in (0, 0xFFFF) or sid == 0x21A3:
                    continue
                x = bx * 8 + int(r[k, 2]); y = by * 8 + int(r[k, 3]); z = int(np.int8(r[k, 4]))
                if WATER_STATIC[0] <= sid <= WATER_STATIC[1]:
                    wet[x, y] = max(wet[x, y], z)
                else:
                    occ[x, y] = True
    return land, lz, wet, occ


# ---------------------------------------------------------------- writing the stage

def write_stage(facet, placed, out: Path, install: Path):
    """Copy statics/staidx of `facet` into `out` with `placed` appended per block.
    Returns the files_override.txt lines. Refuses an `out` inside the install."""
    out = out.resolve(); install = install.resolve()
    if out == install or install in out.parents:
        raise SystemExit(f"refusing to write inside the client install: {out}")
    out.mkdir(parents=True, exist_ok=True)
    by_block = {}
    for x, y, sid, z in placed:
        by_block.setdefault((x >> 3, y >> 3), []).append((sid, x & 7, y & 7, z))
    sta_path = out / f"statics{facet.facet}.mul"
    idx_path = out / f"staidx{facet.facet}.mul"
    sta = bytearray(facet.sta[:])
    idx = bytearray(facet.idx[:])
    for (bx, by), items in sorted(by_block.items()):
        n = facet.number(bx, by)
        off, length = facet.statics_span(n)
        old = bytes(facet.sta[off:off + length]) if length else b""
        extra = struct.unpack_from("<i", idx, n * 12 + 8)[0]
        new = old + b"".join(struct.pack("<HBBbH", sid, x, y, z, 0) for sid, x, y, z in items)
        start = len(sta)
        sta += new
        struct.pack_into("<iii", idx, n * 12, start, len(new), extra)
    sta_path.write_bytes(bytes(sta))
    idx_path.write_bytes(bytes(idx))
    return [f"staidx{facet.facet}.mul={idx_path}", f"statics{facet.facet}.mul={sta_path}"]


TRANSLUCENT = 0x8  # TileFlag.Translucent; ClassicUO draws such statics at alpha 178/255


def stage_translucent_water(install: Path, out: Path) -> str:
    """Copy tiledata.mul into `out` with the water statics 0x1796-0x17B2 flagged Translucent,
    the owner's Transwater convention: the seabed shows through the surface. Returns its
    files_override line. Only the stage copy is written."""
    import shutil
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "uodata_write"))
    import uodata as U
    out = out.resolve(); install = install.resolve()
    if out == install or install in out.parents:
        raise SystemExit(f"refusing to write inside the client install: {out}")
    out.mkdir(parents=True, exist_ok=True)
    dst = out / "tiledata.mul"
    shutil.copyfile(install / "tiledata.mul", dst)
    for i in range(WATER_STATIC[0], WATER_STATIC[1] + 1):
        U.write_tile(dst, i, {"flags": U.read_tile(dst, i)["flags"] | TRANSLUCENT})
    return f"tiledata.mul={dst}"
