"""Seabed generator tests. No client data: a synthetic coast and a fake facet.

    python tools/seabed/test_seabed.py
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import seabed as S  # noqa: E402


def coast(w=64, h=64):
    """Land in the west 16 columns, a dug bed (z -15, water static -5) 24 wide, deep wet land beyond."""
    land = np.full((w, h), 0xA8, np.uint16)
    lz = np.full((w, h), -5, np.int8)
    wet = np.full((w, h), S.NO_STATIC, np.int8)
    land[:16] = 3
    lz[:16] = 0
    land[16:40] = 0x64
    lz[16:40] = -15
    wet[16:40] = -5
    return land, lz, wet, np.zeros((w, h), bool)


def test_rule_and_bands():
    land, lz, wet, occ = coast()
    p = S.plan(land, lz, wet, occ, seed=3)
    assert p.placed, "nothing placed"
    xs = [x for x, y, sid, z, h in p.placed]
    assert min(xs) >= 16                                                          # never on land
    objs = [x for x, y, sid, z, h in p.placed if z == S.OBJECT_Z]
    assert objs and max(objs) <= 16 + S.SHALLOW_MAX                               # no objects past the band
    assert p.eligible_blocks > 0 and p.covered_blocks <= p.eligible_blocks <= p.water_blocks


def test_bed_extended_to_full_band_and_deep_untouched():
    land, lz, wet, occ = coast()
    p = S.plan(land, lz, wet, occ, seed=3)
    # the dug bed (x 16..39) already spans the 14-cell band here; shrink it to 4 cells and it must be extended
    land[20:40] = 0xA8; lz[20:40] = -5; wet[20:40] = S.NO_STATIC
    q = S.plan(land, lz, wet, occ, seed=3)
    assert q.extended_cells > 0 and (q.new_land[20:30] == S.BED_ID).all() and (q.new_lz[20:30] == -15).all()
    assert (q.new_land[16 + S.SHALLOW_MAX + 1:] == 0xA8).all()                    # beyond the band: still deep
    waters = {x for x, y, sid, z, h in q.placed if z == -5}
    assert waters and min(waters) >= 20


def test_floor_fades_to_the_deep_colour():
    land, lz, wet, occ = coast(64, 64)
    p = S.plan(land, lz, wet, occ, seed=4)
    floor = [(x, hue) for x, y, sid, z, hue in p.placed if sid in S.FLOOR]
    near = [h for x, h in floor if x < 19]; far = [h for x, h in floor if x >= 16 + S.SHALLOW_MAX - 2]
    ramp = S.HUE_RAMP
    mean = lambda hs: np.mean([ramp.index(h) for h in hs])
    assert mean(near) < 2.0 and mean(far) > 5.5, (mean(near), mean(far))        # sand at the shore, sea colour at the edge
    # no hard step: the ramp index along x never jumps by more than 2 between column means
    cols = [np.mean([ramp.index(h) for xx, h in floor if xx == c]) for c in range(16, 16 + S.SHALLOW_MAX)]
    assert max(abs(a - b) for a, b in zip(cols, cols[1:])) < 1.2, cols


def test_objects_thin_out_with_depth():
    land, lz, wet, occ = coast(256, 128)
    p = S.plan(land, lz, wet, occ, seed=2)
    obj = [x for x, y, sid, z, h in p.placed if z == S.OBJECT_Z]
    near = sum(1 for x in obj if x < 16 + 5); far = sum(1 for x in obj if x >= 16 + 10)
    assert near > 2 * far and near > 50, (near, far)
    assert all(z in (-5, S.FLOOR_Z, S.OBJECT_Z) for *_, z, h in p.placed)


def test_deterministic_and_seeded():
    land, lz, wet, occ = coast()
    a = S.plan(land, lz, wet, occ, seed=5).placed
    assert a == S.plan(land, lz, wet, occ, seed=5).placed
    assert a != S.plan(land, lz, wet, occ, seed=6).placed


def test_occupied_cells_stay_clear():
    land, lz, wet, occ = coast()
    occ[20:30, :] = True
    p = S.plan(land, lz, wet, occ, seed=3)
    assert not [1 for x, y, sid, z, h in p.placed if 20 <= x < 30 and sid not in S.FLOOR]   # floor yes, objects no


def test_groups_not_uniform():
    land, lz, wet, occ = coast(128, 128)
    p = S.plan(land, lz, wet, occ, seed=2)
    ids = {sid for x, y, sid, z, h in p.placed}
    assert ids & set(S.PLANTS) and ids & set(S.ROCKS_SMALL + S.ROCKS_CLUSTER)
    dens = np.zeros((16, 16))
    for x, y, sid, z, h in p.placed:
        if z == S.OBJECT_Z:
            dens[x // 8, y // 8] += 1
    band = dens[2:4]
    assert band.std() > 0.5   # clumps, not an even sprinkle


def test_deep_water_untouched():
    land = np.full((64, 32), 0xA8, np.uint16); lz = np.full((64, 32), -5, np.int8)
    land[:2] = 3; lz[:2] = 0
    wet = np.full((64, 32), S.NO_STATIC, np.int8)
    p = S.plan(land, lz, wet, np.zeros((64, 32), bool))
    far = 2 + S.SHALLOW_MAX
    assert all(x < far for x, y, sid, z, h in p.placed)                      # nothing past the band
    assert (p.new_land[far:] == 0xA8).all() and (p.new_lz[far:] == -5).all()  # deep sea keeps its land


def test_trim_overlap_makes_a_seamless_tiling():
    h = w = 44
    mask = np.zeros((h, w), bool)
    for y in range(h):
        half = (y + 1) if y < 22 else (h - y)
        mask[y, 22 - half:22 + half] = True               # the 44x44 diamond: 1012 pixels, rows 2..44..2
    assert mask.sum() == 1012
    t = S.trim_overlap(mask)
    cover = np.zeros((400, 400), int)
    for dx in range(-3, 4):
        for dy in range(-3, 4):
            cover[200 + (dx + dy) * 22:200 + (dx + dy) * 22 + h, 200 + (dx - dy) * 22:200 + (dx - dy) * 22 + w] += t
    inner = cover[190:230, 190:230]
    assert (inner == 1).all(), (int((inner > 1).sum()), int((inner == 0).sum()))   # no overlap, no gap


class FakeFacet:
    facet = 0
    def __init__(self):
        self.width_blocks = self.height_blocks = 2
        self.idx = bytearray(struct.pack("<iii", 0, 7, 0) + b"".join(struct.pack("<iii", -1, 0, 0) for _ in range(3)))
        self.sta = bytearray(struct.pack("<HBBbH", 0x0CD0, 1, 1, 0, 0))
    def number(self, bx, by): return bx * 2 + by
    def statics_span(self, n):
        o, l, _ = struct.unpack_from("<iii", self.idx, n * 12)
        return (o, l) if l > 0 else (0, 0)


def test_writer_keeps_old_and_refuses_install(tmp_path):
    f = FakeFacet()
    S.write_stage(f, [(2, 3, 0xD04, -14, 0)], tmp_path / "stage", tmp_path / "install")
    sta = (tmp_path / "stage" / "statics0.mul").read_bytes()
    idx = (tmp_path / "stage" / "staidx0.mul").read_bytes()
    off, length, _ = struct.unpack_from("<iii", idx, 0)
    recs = [struct.unpack_from("<HBBbH", sta, off + i * 7) for i in range(length // 7)]
    assert recs == [(0xCD0, 1, 1, 0, 0), (0xD04, 2, 3, -14, 0)]
    for bad in (tmp_path / "install", tmp_path / "install" / "sub"):
        try:
            S.write_stage(f, [], bad, tmp_path / "install")
        except SystemExit:
            continue
        raise AssertionError("wrote inside the install")


def main():
    import tempfile
    names = [n for n in globals() if n.startswith("test_")]
    for n in names:
        fn = globals()[n]
        if fn.__code__.co_argcount:
            with tempfile.TemporaryDirectory() as d:
                fn(Path(d))
        else:
            fn()
        print(f"ok {n}")
    print(f"{len(names)} passed")


if __name__ == "__main__":
    main()
