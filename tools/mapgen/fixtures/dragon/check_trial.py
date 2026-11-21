"""Verify every terrain cell written by Dragon after running make_trial.py."""
import argparse
import hashlib
import struct
from collections import Counter
from functools import lru_cache
from pathlib import Path


def check(path, reverse, random_tiles, superset_only, edge_lift):
    raw = path.read_bytes()
    if len(raw) != 12 * 196:
        raise ValueError(f"Expected 12 native map blocks, got {len(raw)} bytes")
    counts = Counter()
    cells = bytearray()
    for y in range(24):
        for x in range(32):
            block = (x // 8) * 3 + y // 8
            offset = block * 196 + 4 + ((y % 8) * 8 + x % 8) * 3
            tile, z = struct.unpack_from("<Hb", raw, offset)
            ids = {3, 4} if random_tiles else {3}
            height = 19 if (x, y) == (7, 9) else 7
            if x >= 17:
                ids, height = {22}, -5
            elif x == 16 and 0 < y < 23 and not superset_only:
                ids = {56 if reverse else 57}
                if edge_lift:
                    height += 3
            if tile not in ids or z != height:
                raise ValueError(f"({x},{y}) expected {ids} at {height}, got {tile} at {z}")
            cells.extend(raw[offset:offset + 3])
            counts[tile] += 1
    print(f"768 cells passed. Tile counts {dict(sorted(counts.items()))}. "
          f"Row-major cell SHA-256 {hashlib.sha256(cells).hexdigest()}")


def check_frozen(folder):
    index = (folder / "staidx0.mul").read_bytes()
    statics = (folder / "statics0.mul").read_bytes()
    if len(index) != 768 * 512 * 12 or len(statics) != 9 * 7:
        raise ValueError("Unexpected frozen file sizes for the bundled DragonSP 1.00.00")
    found = []
    for block, (start, size, _) in enumerate(struct.iter_unpack("<iii", index)):
        if size <= 0:
            continue
        if start < 0 or start + size > len(statics):
            raise ValueError("Static index points outside the data file")
        for tile, dx, dy, z, hue in struct.iter_unpack("<HBBbH", statics[start:start + size]):
            found.append((8 * (block // 512) + dx, 8 * (block % 512) + dy, tile, z, hue))
    expected = [(x, y, 0x0EED, 21 if (x, y) == (7, 9) else 9, 0x21)
                for x in range(7, 10) for y in range(7, 10)]
    if sorted(found) != expected:
        raise ValueError(f"Frozen placements differ: {sorted(found)}")
    print("9 frozen placements passed, including coordinates, ID, signed Z and hue.")


def check_classic(path, reverse):
    """Compare whole block payloads to reuse the identical offshore blocks of a full facet."""
    @lru_cache(maxsize=None)
    def expected_block(bx, by):
        expected = bytearray()
        for dy in range(8):
            for dx in range(8):
                x, y = 8 * bx + dx, 8 * by + dy
                tile, z = (22, -5) if x >= 17 else (3, 7)
                if (x, y) == (7, 9):
                    z = 19
                if x == 16 and 0 < y < 4095:
                    tile = 57 if reverse else 56
                expected.extend(struct.pack("<Hb", tile, z))
        return expected

    with path.open("rb") as stream:
        for bx in range(768):
            for by in range(512):
                block = stream.read(196)
                template_y = by if by in (0, 1, 511) else 2
                if block[4:] != expected_block(min(bx, 3), template_y):
                    raise ValueError(f"Unexpected 1.03.62 land cells in block ({bx},{by})")
        if stream.read(1):
            raise ValueError("Unexpected trailing bytes in full-facet map")
    print("25,165,824 native 1.03.62 cell IDs and signed heights passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("map", type=Path)
    parser.add_argument("--reverse", action="store_true")
    parser.add_argument("--random-tiles", action="store_true")
    parser.add_argument("--superset-only", action="store_true")
    parser.add_argument("--edge-lift", action="store_true")
    parser.add_argument("--frozen", action="store_true", help="also verify DragonSP output beside the map")
    parser.add_argument("--classic", action="store_true", help="verify the full 1.03.62 base or reversed-rule trial")
    args = parser.parse_args()
    if args.classic:
        if args.random_tiles or args.superset_only or args.edge_lift or args.frozen:
            parser.error("--classic supports the base and --reverse checks")
        check_classic(args.map, args.reverse)
    else:
        check(args.map, args.reverse, args.random_tiles, args.superset_only, args.edge_lift)
    if args.frozen:
        check_frozen(args.map.parent)
