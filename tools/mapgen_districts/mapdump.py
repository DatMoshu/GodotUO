"""Read a map generator run's map.bin (tools/mapgen/cli/Render.cs MapDump) and pick district lots."""
from __future__ import annotations

import struct
from pathlib import Path

import numpy as np

# MapGen/IR/BiomeId.cs: the dry, buildable ground a district may replace.
BUILDABLE = {4, 5, 6, 8, 18}      # Grassland, Forest, DenseForest, Savanna, Road (redrawn to the town)
ROAD = 18


class MapDump:
    def __init__(self, path: Path):
        raw = Path(path).read_bytes()
        self.width, self.height = struct.unpack_from("<ii", raw, 0)
        n = self.width * self.height
        cells = np.frombuffer(raw, dtype=np.dtype([("land", "<u2"), ("z", "i1"), ("biome", "u1")]), count=n, offset=8)
        self.land = cells["land"].reshape(self.height, self.width)
        self.z = cells["z"].reshape(self.height, self.width).astype(np.int16)
        self.biome = cells["biome"].reshape(self.height, self.width)


def pick_lots(dump: MapDump, size: int, count: int, spacing: int, margin: int = 24, max_relief: int = 8):
    """Up to `count` lots of size x size, x/y on the 8-cell block grid, all on buildable ground with
    at most `max_relief` of height range, at least `spacing` apart (centre to centre, Chebyshev).
    Lowest relief first, then nearest the map's middle, so the choice is deterministic."""
    ok = np.isin(dump.biome, list(BUILDABLE))
    candidates = []
    for y in range(margin - margin % 8 + 8, dump.height - size - margin, 8):
        for x in range(margin - margin % 8 + 8, dump.width - size - margin, 8):
            if not ok[y:y + size, x:x + size].all():
                continue
            zs = dump.z[y:y + size, x:x + size]
            relief = int(zs.max() - zs.min())
            if relief > max_relief:
                continue
            mid = abs(x + size / 2 - dump.width / 2) + abs(y + size / 2 - dump.height / 2)
            candidates.append((relief, int(abs(np.median(zs))), mid, x, y))
    candidates.sort()
    chosen = []
    for relief, zmed, _, x, y in candidates:
        if all(max(abs(x - cx), abs(y - cy)) >= spacing for cx, cy, *_ in chosen):
            chosen.append((x, y, relief, zmed))
            if len(chosen) == count:
                break
    return chosen
