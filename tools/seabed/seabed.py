"""Seabed layout for a UO facet: pure planning plus the stage writer.

The shallows of Felucca are land tiles dug below the water statics (bed
0x4C-0x64 at z -15, water at z -5), and nothing lives on them. They also stop
at an arbitrary line, where the deep sea (flat wet land, opaque) begins. This
module defines the shallow band, extends the bed to its full width, lays a floor
whose colour fades into the deep, and plants sea plants, corals and rocks on it.

Nothing here writes to the install: the writer copies the files it patches into
a stage folder and refuses a stage inside the install.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

WET_LAND = (0xA8, 0xA9, 0xAA, 0xAB, 0x136, 0x137)
WATER_STATIC = (0x1796, 0x17B2)
NO_STATIC = -127

# Client art (tiledata names in the comments). Sources in tools/seabed/README.md.
ROCKS_SMALL = (0x1363, 0x1364, 0x1365, 0x1368, 0x1369, 0x136B, 0x136C, 0x136D)  # "rock"/"rocks"
ROCKS_CLUSTER = (0x1367, 0x136A)                        # "rocks": 2-3 stones
BOULDERS = (0x1771, 0x1772, 0x1773, 0x1774, 0x1775, 0x1776)  # large brown/grey "rock"
PLANTS = (0xD04,)                                       # "water plants" (0xD05 "reeds" carries a dark diamond base: left out)
CORAL = (0x4C2A, 0x4C2B, 0x4C2C, 0x4C2D, 0x4C2E, 0x4C2F, 0x4C30, 0x4C31)  # "Sea Coral"/"Sea Sponge"
CORAL_B = (0xA38B, 0xA38D, 0xA38E, 0xA38F, 0xA386)      # fire/staghorn/plate/red coral, anemone
SHELLS = (0xFC4, 0xFC5, 0xFC6, 0xFC8, 0xFC9, 0xFCA, 0xFCB, 0xFCC)
FLOOR = (0x9DE9, 0x9DEA, 0x9DEB, 0x9DEC)                # "sand_tile": a seamless sand diamond
WATER_FILL = (0x1797, 0x1798, 0x1799, 0x179A, 0x179B)   # interior water statics, as on Felucca's own beds

SHALLOW_MAX = 14   # cells from dry land over which the shallows fade out into the deep
# Hue ramp for the floor, shore to deep (hues.mul ids), with each hue's progress along the straight line
# from the shore colour (hue 1151, as photographed) to the open sea's own colour, as the client showed it
# (calibrate.py: 64 hues photographed in place, fit residual 1.5 colour units; the original opaque
# deep water measured (0, 54, 80) at Haven). The hue table has no colour on that line between 0 and
# 0.17 and none past 0.8 that is smooth (1916 matches the deep but sparkles; the dark smooth ones read as
# scattered navy diamonds), so plan() mixes neighbours by probability and, past the last hue, leaves
# deep cells as the original sea by probability: the fade ends on the sea itself. Hues whose palette
# speckles (1593, 1599, 1469, 1916) or tints (2571 purple) are left out by calibrate.choose_ramp.
HUE_RAMP = (1151, 191, 391, 396, 490, 495, 589, 594, 494, 688, 2757, 593, 587, 392, 592)
RAMP_PROGRESS = (0.0, 0.171, 0.183, 0.229, 0.304, 0.362, 0.437, 0.492, 0.51, 0.578, 0.618, 0.632, 0.728, 0.757, 0.788)
PLAIN_FLOOR_HUE = 191   # the floor's one hue when the client fades the surface itself (plan(shader=True))
FLOOR_Z = -15
OBJECT_Z = -14     # above the floor tile so it never draws over them
BED_ID = 0x64
BED_FIRST, BED_LAST = 0x4C, 0x64


@dataclass
class Plan:
    """What the generator decided: statics as (x, y, id, z, hue) in facet cells, plus the land
    it turned into seabed (new_land/new_lz) where the dug beds stopped short of the deep."""
    placed: list
    new_land: object
    new_lz: object
    extended_cells: int
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


def plan(land, land_z, wet_z, occupied, seed=1, origin=(0, 0), shallow_max=SHALLOW_MAX, full_water=None, calibrate=None,
         shader=False):
    """Plan the seabed. Arrays are [x, y]. `occupied` marks cells holding a non-water static
    (piers, boats, ruins); they get a floor but no seabed objects.

    Shallow rule: a water cell no further than `shallow_max` cells (Chebyshev) from dry land,
    over a dug bed or over deep wet land that is turned into the same bed (land 0x64 at z -15
    under a water static at z -5), so the shallows reach their full width. Every shallow cell
    gets a floor tile whose hue steps from plain sand at the shore to the colour of the open
    sea at `shallow_max`, dithered by noise, so no edge shows between shallows and deep.
    Plants, corals, rocks and shells sit on the floor in groups whose density falls off with
    the same distance. `full_water` marks cells that already carry a whole water tile (0x1797-0x179C);
    the others in the band (the near-empty 0x1796 and shore-shaped edges Felucca stacks along its
    beds) get one more at z -5, or the floor would show through bare.
    `calibrate` (calibrate.py) replaces the floor hues by a function (x, y arrays) -> hues and
    drops every object, so the client can photograph what each hue looks like in place.
    `shader` plans for a client that fades the surface with depth and blends each water pixel once
    itself (uo_water.gdshaderinc, DEPTH FADE and ONE TILE A PIXEL): one floor hue, no cells handed
    back to the opaque sea and no edge strips. Per-cell hues read as diamonds under a water style.
    """
    water, bed = water_masks(land, land_z, wet_z)
    w, h = land.shape
    dist = shore_distance(water)
    wet_land = np.isin(land, WET_LAND)
    wet_static = wet_z > NO_STATIC
    ext = wet_land & (dist <= shallow_max)               # deep land turned into bed (with or without a surface already)
    bed_land = (land >= BED_FIRST) & (land <= BED_LAST)
    lowered = wet_static & ~wet_land & bed_land & ~bed   # Felucca's bed rows raised to the surface: sunk to bed depth
    xs = np.arange(w)[:, None] * np.ones((1, h), np.int64) + origin[0]
    ys = np.ones((w, 1), np.int64) * np.arange(h)[None, :] + origin[1]
    # Past the last ramp hue the floor stops: a deep cell (not a dug bed) stays the original opaque sea
    # with a probability that rises to 1 at the band's edge.
    back = np.clip((dist.astype(np.float64) / shallow_max - RAMP_PROGRESS[-1]) / (1.0 - RAMP_PROGRESS[-1]), 0.0, 1.0)
    if not shader:
        ext = ext & ~(~wet_static & (_hash2(xs, ys, seed + 19) < back))
    shallow = bed | lowered | ext
    floor_z = np.where(wet_static, wet_z.astype(np.int16) - 10, FLOOR_Z).astype(np.int16)
    r = _hash2(xs, ys, seed)
    r2 = _hash2(xs, ys, seed + 7)
    r3 = _hash2(xs, ys, seed + 13)
    meadow = _value_noise(w, h, 9, seed + 1)
    rocky = _value_noise(w, h, 7, seed + 2)
    sandy = _value_noise(w, h, 13, seed + 3)

    t = np.clip(dist.astype(np.float64) / shallow_max, 0.0, 1.0)   # 0 at the shore, 1 in the deep
    fade = (1.0 - t) ** 1.5                                        # object density falls to 0 smoothly
    prog = np.asarray(RAMP_PROGRESS)
    nb = len(HUE_RAMP) - 1
    t_adj = np.clip(t - np.clip(sandy - 0.55, 0, None) * 0.57, 0.0, 1.0)   # sand patches: lighter, soft
    lo = np.clip(np.searchsorted(prog, t_adj, side="right") - 1, 0, nb - 1)
    frac = np.clip((t_adj - prog[lo]) / (prog[lo + 1] - prog[lo]), 0.0, 1.0)
    level = np.where(r3 < frac, lo + 1, lo).astype(int)   # stochastic rounding: the mean colour is exact, never a band
    hue_at = np.asarray(HUE_RAMP)[level]
    if shader:
        hue_at = np.full((w, h), PLAIN_FLOOR_HUE)
    if calibrate is not None:
        hue_at = calibrate(xs, ys)
    kind = np.zeros((w, h), np.uint8)   # 0 none 2 rock 3 boulder 4 plant 5 coral 6 shell
    free = shallow & (dist >= 1) & ~occupied & (calibrate is None)
    rock_zone = free & (rocky > 0.6)
    kind[rock_zone & (r2 < 0.55 * fade)] = 2
    kind[rock_zone & (r2 < 0.10 * fade) & (dist >= 3)] = 3
    plant_zone = free & (meadow > 0.45) & (kind == 0)
    kind[plant_zone & (r < 0.55 * fade)] = 4
    coral_zone = free & (meadow < 0.34) & (kind == 0) & (dist >= 3)
    kind[coral_zone & (r2 < 0.30 * fade)] = 5
    kind[free & (kind == 0) & (r < 0.02 * fade)] = 6

    new_land = land.copy(); new_lz = land_z.copy()
    new_land[ext] = BED_ID
    new_lz[ext] = floor_z[ext]
    new_lz[lowered] = floor_z[lowered]

    placed = []
    need = shallow & (dist >= 2) & ((ext & ~wet_static) | (wet_static & ~(full_water if full_water is not None else wet_static)))
    ix, iy = np.nonzero(need)
    for x, y in zip(ix.tolist(), iy.tolist()):
        placed.append((x, y, WATER_FILL[int(r[x, y] * len(WATER_FILL))], int(wet_z[x, y]) if wet_static[x, y] else -5, 0))
    ix, iy = np.nonzero(shallow)
    for x, y in zip(ix.tolist(), iy.tolist()):
        placed.append((x, y, FLOOR[int(r2[x, y] * len(FLOOR))], int(floor_z[x, y]), int(hue_at[x, y])))
    whole = need | (full_water if full_water is not None else wet_static)   # cells that now carry a whole (trimmed) water tile
    water_z = np.where(wet_static, wet_z, -5)
    for (cx, cy), sid in (() if shader else zip(STRIP_CELLS, STRIP_IDS)):
        has = np.zeros_like(whole)   # has[x, y]: the neighbour at (x + cx, y + cy) carries a whole water tile
        xa, xb = max(0, -cx), w - max(0, cx)
        ya, yb = max(0, -cy), h - max(0, cy)
        has[xa:xb, ya:yb] = whole[xa + cx:xb + cx, ya + cy:yb + cy]   # shore-shaped edge tiles never draw these edge pixels (measured: <= 55% of one strip)
        ix, iy = np.nonzero(whole & ~has)
        for x, y in zip(ix.tolist(), iy.tolist()):
            placed.append((x, y, sid, int(water_z[x, y]), 0))
    for k in (2, 3, 4, 5, 6):
        ix, iy = np.nonzero(kind == k)
        for x, y in zip(ix.tolist(), iy.tolist()):
            placed.append((x, y, _pick_id(k, _pick_unit(x + origin[0], y + origin[1], seed, k)), int(floor_z[x, y]) + 1, 0))

    def blocks(mask):
        return int(mask[: w // 8 * 8, : h // 8 * 8].reshape(w // 8, 8, h // 8, 8).any(axis=(1, 3)).sum())
    return Plan(placed, new_land, new_lz, int(ext.sum()), int(water.sum()), int(shallow.sum()),
                blocks(water), blocks(shallow), blocks(kind > 0))


def _pick_unit(x, y, seed, k):
    return float(_hash2(np.array([x]), np.array([y]), seed * 31 + k * 101)[0])


def _pick_id(k, u):
    def pick(seq):
        return seq[min(int(u * len(seq)), len(seq) - 1)]
    if k == 2:
        return pick(ROCKS_SMALL if u < 0.7 else ROCKS_CLUSTER)
    if k == 3:
        return pick(BOULDERS)
    if k == 4:
        return PLANTS[0]
    if k == 5:
        return pick(CORAL if u < 0.7 else CORAL_B)
    return pick(SHELLS)


# ---------------------------------------------------------------- reading a facet

def scan_facet(facet):
    """Read a uomap.Facet into [x, y] arrays: land id, land z, wet static z, and
    cells holding any other (non-water) static, and cells carrying a whole water tile."""
    W, H = facet.width_blocks * 8, facet.height_blocks * 8
    land = np.zeros((W, H), np.uint16)
    lz = np.zeros((W, H), np.int8)
    wet = np.full((W, H), NO_STATIC, np.int8)
    occ = np.zeros((W, H), bool)
    full = np.zeros((W, H), bool)
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
                if sid in (0, 0xFFFF) or sid in (0x21A3, 0x21A4) or (sid == 0x45 and int(np.int8(r[k, 4])) < -40):
                    continue   # blank markers and the arch Felucca hides at z -88: not objects
                x = bx * 8 + int(r[k, 2]); y = by * 8 + int(r[k, 3]); z = int(np.int8(r[k, 4]))
                if WATER_STATIC[0] <= sid <= WATER_STATIC[1]:
                    wet[x, y] = max(wet[x, y], z)
                    if 0x1797 <= sid <= 0x179C:
                        full[x, y] = True
                else:
                    occ[x, y] = True
    return land, lz, wet, occ, full


# ---------------------------------------------------------------- writing the stage

def _inside(out: Path, install: Path) -> bool:
    out = out.resolve(); install = install.resolve()
    return out == install or install in out.parents


def _write(path: Path, data) -> None:
    """write_bytes in 4 MiB pieces: one big write has failed with EINVAL on a synced build drive."""
    view = memoryview(data)
    with path.open("wb") as f:
        for i in range(0, len(view), 4 << 20):
            f.write(view[i:i + (4 << 20)])


def write_stage(facet, placed, out: Path, install: Path, new_land=None, new_lz=None, old_land=None, old_lz=None):
    """Copy statics/staidx of `facet` into `out` with `placed` appended per block and, when
    new_land/new_lz are given, a patched copy of the facet's land file (only the blocks
    that changed). Returns the files_override.txt lines. Refuses an `out` inside the install."""
    if _inside(out, install):
        raise SystemExit(f"refusing to write inside the client install: {out}")
    out = out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    by_block = {}
    for x, y, sid, z, hue in placed:
        by_block.setdefault((x >> 3, y >> 3), []).append((sid, x & 7, y & 7, z, hue))
    sta_path = out / f"statics{facet.facet}.mul"
    idx_path = out / f"staidx{facet.facet}.mul"
    sta = bytearray(facet.sta[:])
    idx = bytearray(facet.idx[:])
    for (bx, by), items in sorted(by_block.items()):
        n = facet.number(bx, by)
        off, length = facet.statics_span(n)
        old = bytes(facet.sta[off:off + length]) if length else b""
        extra = struct.unpack_from("<i", idx, n * 12 + 8)[0]
        new = old + b"".join(struct.pack("<HBBbH", sid, x, y, z, hue) for sid, x, y, z, hue in items)
        start = len(sta)
        sta += new
        struct.pack_into("<iii", idx, n * 12, start, len(new), extra)
    _write(sta_path, sta)
    _write(idx_path, idx)
    lines = [f"staidx{facet.facet}.mul={idx_path}", f"statics{facet.facet}.mul={sta_path}"]
    if new_land is not None:
        import shutil
        src = Path(facet.map_path)
        dst = out / src.name
        shutil.copyfile(src, dst)
        diff = (new_land != old_land) | (new_lz != old_lz)
        blocks = diff.reshape(diff.shape[0] // 8, 8, diff.shape[1] // 8, 8).any(axis=(1, 3))
        with dst.open("r+b") as f:
            for bx, by in zip(*np.nonzero(blocks)):
                bx, by = int(bx), int(by)
                ids = new_land[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8].T.reshape(64)
                zs = new_lz[bx * 8:bx * 8 + 8, by * 8:by * 8 + 8].T.reshape(64)
                f.seek(facet.map_offset(facet.number(bx, by)) + 4)
                f.write(b"".join(struct.pack("<Hb", int(i), int(z)) for i, z in zip(ids, zs)))
        lines.append(f"{src.name.lower()}={dst}")
    return lines


TRANSLUCENT = 0x8  # TileFlag.Translucent; ClassicUO draws such statics at alpha 178/255


def stage_translucent_water(install: Path, out: Path, strips: bool = True) -> str:
    """Copy tiledata.mul into `out` with the water statics 0x1796-0x17B2 flagged Translucent,
    the owner's Transwater convention: the seabed shows through the surface. Returns its
    files_override line. Only the stage copy is written."""
    import shutil
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "uodata_write"))
    import uodata as U
    if _inside(out, install):
        raise SystemExit(f"refusing to write inside the client install: {out}")
    out = out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    dst = out / "tiledata.mul"
    shutil.copyfile(install / "tiledata.mul", dst)
    for i in range(WATER_STATIC[0], WATER_STATIC[1] + 1):
        U.write_tile(dst, i, {"flags": U.read_tile(dst, i)["flags"] | TRANSLUCENT})
    for i in (STRIP_IDS if strips else ()):   # the edge strips (stage_seamless_water_art): water that is translucent, with a name
        U.write_tile(dst, i, {**U.read_tile(dst, 0x1797), "name": "water edge", "flags": U.read_tile(dst, 0x1797)["flags"] | TRANSLUCENT})
    return f"tiledata.mul={dst}"


# ---------------------------------------------------------------- seamless translucent water

FULL_WATER = (0x1796, 0x179C)   # 0x1797-0x179C are whole diamonds (1012 pixels); 0x1796 is only their outline
_UP_NEIGHBOURS = ((-22, -22), (22, -22), (-44, 0), (0, -44))


# The cell offset of each of those neighbours (screen (dx, dy) / 22 -> (x - y, x + y) -> cell).
STRIP_CELLS = ((-1, 0), (0, -1), (-1, 1), (-1, -1))
STRIP_IDS = (0xFFFD, 0xFFFC, 0xFFFB, 0xFFFA)   # free static ids (no art, empty tiledata): the edge strips below


def trim_parts(mask):
    """(kept, strips): the tile minus the pixels a neighbour above or beside it already draws,
    and for each of those neighbours (in _UP_NEIGHBOURS order) the pixels it was the one to
    take. Neighbouring diamonds overlap by a pixel row along each edge; opaque that is invisible,
    but a translucent surface blends twice there and shows a line."""
    h, w = mask.shape
    out = mask.copy()
    strips = []
    for dx, dy in _UP_NEIGHBOURS:
        strip = np.zeros_like(mask)
        for y in range(h):
            for x in range(w):
                nx, ny = x - dx, y - dy   # this pixel in the neighbour's frame
                if out[y, x] and 0 <= nx < w and 0 <= ny < h and mask[ny, nx]:
                    out[y, x] = False
                    strip[y, x] = True
        strips.append(strip)
    return out, strips


def trim_overlap(mask):
    """The tile without the pixels a neighbour above or beside it already draws (see trim_parts)."""
    return trim_parts(mask)[0]


def stage_seamless_water_art(install: Path, out: Path) -> str:
    """Copy artLegacyMUL.uop into `out` with the whole-diamond water statics trimmed by
    trim_overlap (in place in the copy; the install is untouched). Returns its override line."""
    import shutil
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "uodata_write"))
    import uodata as U
    from guo import uoart
    if _inside(out, install):
        raise SystemExit(f"refusing to write inside the client install: {out}")
    out = out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    src, dst = install / "artLegacyMUL.uop", out / "artLegacyMUL.uop"
    shutil.copyfile(src, dst)
    wanted = {U.uop_hash(U.ART_PATTERN.format(0x4000 + i)): i for i in range(FULL_WATER[0], FULL_WATER[1] + 1)}
    source = {}
    with dst.open("r+b") as f:
        data = f.read()
        magic, _v, _st, block, _bs, _count = struct.unpack_from("<IIIqIi", data, 0)
        while block:
            files, nxt = struct.unpack_from("<iq", data, block)
            pos = block + 12
            for _ in range(files):
                offset, header, comp, decomp, h, _crc, flag = struct.unpack_from("<qiiiQIh", data, pos)
                if h in wanted and offset:
                    w, hh, px = uoart.decode_static(data[offset + header:offset + header + decomp])
                    mask = np.array(px, dtype=np.uint16).reshape(hh, w) > 0
                    keep = trim_overlap(mask) if wanted[h] != 0x1796 else np.zeros_like(mask)   # the outline tile is the seam itself
                    if wanted[h] == 0x1797:
                        source = {"w": w, "h": hh, "px": px, "mask": mask}
                    px2 = [p if keep[i // w, i % w] else 0 for i, p in enumerate(px)]
                    new = uoart.encode_static(px2, w, hh)
                    if flag != 0 or len(new) > decomp:
                        raise SystemExit(f"cannot trim art {wanted[h]:#x} in place")
                    f.seek(offset + header)
                    f.write(new)
                    f.seek(pos + 12)
                    f.write(struct.pack("<ii", len(new), len(new)))
                pos += 34
            block = nxt
    # The trimmed edge is a gap wherever the neighbour that would draw it has no water (a coast, the
    # edge of a bed): thin light lines on the beach. Four extra translucent tiles hold exactly the
    # pixels each neighbour takes; plan() places one on a water cell whose neighbour is missing.
    _, strips = trim_parts(source["mask"])
    items = []
    for sid, strip in zip(STRIP_IDS, strips):
        px2 = [p if strip[i // source["w"], i % source["w"]] else 0 for i, p in enumerate(source["px"])]
        items.append((U.ART_PATTERN.format(0x4000 + sid), uoart.encode_static(px2, source["w"], source["h"])))
    U.uop_append(dst, items)
    return f"artlegacymul.uop={dst}"
