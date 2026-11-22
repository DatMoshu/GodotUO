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
    xs = [x for x, *_ in p.placed]
    assert min(xs) >= 16 and max(xs) < 16 + S.SHALLOW_MAX, (min(xs), max(xs))   # never land, never deep
    assert all(z == -15 for *_, z in p.placed)                                  # on the bed
    assert len({(x, y) for x, y, *_ in p.placed}) == len(p.placed)              # one per cell
    assert p.eligible_blocks > 0 and p.covered_blocks <= p.eligible_blocks <= p.water_blocks


def test_deterministic_and_seeded():
    land, lz, wet, occ = coast()
    a = S.plan(land, lz, wet, occ, seed=5).placed
    assert a == S.plan(land, lz, wet, occ, seed=5).placed
    assert a != S.plan(land, lz, wet, occ, seed=6).placed


def test_occupied_cells_stay_clear():
    land, lz, wet, occ = coast()
    occ[20:30, :] = True
    p = S.plan(land, lz, wet, occ, seed=3)
    assert not [1 for x, *_ in p.placed if 20 <= x < 30]


def test_groups_not_uniform():
    land, lz, wet, occ = coast(128, 128)
    p = S.plan(land, lz, wet, occ, seed=2)
    ids = {sid for *_, sid, _ in p.placed}
    assert ids & set(S.PLANTS) and ids & set(S.ROCKS_SMALL + S.ROCKS_CLUSTER) and ids & set(S.SAND_RIPPLE)
    dens = np.zeros((128 // 8, 128 // 8))
    for x, y, *_ in p.placed:
        dens[x // 8, y // 8] += 1
    band = dens[2:5]
    assert band.max() > 3 * max(band.mean(), 1e-9) * 0.5 and band.std() > 0.5   # clumps, not an even sprinkle


def test_deep_water_untouched():
    land = np.full((32, 32), 0xA8, np.uint16); lz = np.full((32, 32), -5, np.int8)
    wet = np.full((32, 32), S.NO_STATIC, np.int8)
    p = S.plan(land, lz, wet, np.zeros((32, 32), bool))
    assert not p.placed and p.eligible_blocks == 0 and p.water_blocks == 16


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
    S.write_stage(f, [(2, 3, 0xD04, -15)], tmp_path / "stage", tmp_path / "install")
    sta = (tmp_path / "stage" / "statics0.mul").read_bytes()
    idx = (tmp_path / "stage" / "staidx0.mul").read_bytes()
    off, length, _ = struct.unpack_from("<iii", idx, 0)
    recs = [struct.unpack_from("<HBBbH", sta, off + i * 7) for i in range(length // 7)]
    assert recs == [(0xCD0, 1, 1, 0, 0), (0xD04, 2, 3, -15, 0)]
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
