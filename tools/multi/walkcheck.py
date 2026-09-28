"""Can a walker get from each tour stop to the next? An offline check of a built scene.

A rough model of UO movement, enough to catch a stair that lands behind a parapet or a floor
that is missing before a proof spends twenty minutes finding it:
- a cell can be stood on at the top of a surface piece (half its height for a bridge, as the
  client counts stairs), when nothing impassable stands in the 16 z above it;
- a walker moves to any of the eight neighbours, up at most a step (5 z; a stair's pieces
  rise 5 at a time), or down any distance (a walker can drop off a ledge); a diagonal step
  also needs both cells beside it open at about that height (UO does not cut corners);
- the land counts as a surface at the scene's ground wherever nothing covers it.
The shard and the client are the proof; this only says where to look.
"""
from __future__ import annotations

import heapq

HEADROOM = 16
CLIMB = 5


def surfaces(parts: list[dict], pieces: dict, ground: int = 0) -> tuple[dict, set]:
    """{(x, y): [standing z, ...]} and the set of cells anything stands in."""
    stand: dict[tuple, set] = {}
    solid: dict[tuple, list] = {}
    covered = set()
    for p in parts:
        cx, cy = p["centre"][:2]
        for c in p["comps"]:
            if not c.visible:
                continue
            info = pieces.get(f"{c.item:#06x}", {})
            flags, h = info.get("flags", []), info.get("height") or 0
            at = (c.x + cx, c.y + cy)
            covered.add(at)
            if "surface" in flags:
                stand.setdefault(at, set()).add(c.z + (h // 2 if "bridge" in flags else h))
            if "impassable" in flags:
                solid.setdefault(at, []).append((c.z, c.z + h))
    out = {}
    for at in set(stand) | covered:
        zs = set(stand.get(at, ()))
        if not any(a <= ground < b for a, b in solid.get(at, ())):
            zs.add(ground)
        free = sorted(z for z in zs if not any(a < z + HEADROOM and b > z for a, b in solid.get(at, ())))
        if free:
            out[at] = free
    return out, covered


def path(stand: dict, covered: set, start: tuple, goal: tuple, ground: int = 0, slack: int = 4) -> bool:
    """A walk from (x, y, z) to (x, y, z within slack), over the scene and the open land round it."""
    def spots(at):
        if at in stand:
            return stand[at]
        return [ground] if at not in covered else []

    sz = min(spots(start[:2]), key=lambda z: abs(z - start[2]), default=None)
    if sz is None:
        return False
    first = (start[0], start[1], sz)
    seen = {first}
    heap = [(0, first)]
    xs = [x for x, _ in covered] or [start[0]]
    ys = [y for _, y in covered] or [start[1]]
    lo_x, hi_x, lo_y, hi_y = min(xs) - 4, max(xs) + 4, min(ys) - 4, max(ys) + 4
    while heap:
        _, (x, y, z) = heapq.heappop(heap)
        if (x, y) == goal[:2] and abs(z - goal[2]) <= slack:
            return True
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                n = (x + dx, y + dy)
                if (dx, dy) == (0, 0) or not (lo_x <= n[0] <= hi_x and lo_y <= n[1] <= hi_y):
                    continue
                for nz in spots(n):
                    if nz - z > CLIMB:
                        continue
                    if dx and dy and not all(any(abs(sz - max(z, nz)) <= CLIMB or min(z, nz) <= sz <= max(z, nz)
                                                 for sz in spots(side))
                                             for side in ((x + dx, y), (x, y + dy))):
                        continue
                    k = (n[0], n[1], nz)
                    if k not in seen:
                        seen.add(k)
                        est = abs(n[0] - goal[0]) + abs(n[1] - goal[1]) + abs(nz - goal[2]) // 5
                        heapq.heappush(heap, (est, k))
    return False


def check_tour(parts: list[dict], tour: list[dict], pieces: dict, ground: int = 0) -> list[str]:
    """Each leg of the tour that has no walk, as a problem line."""
    stand, covered = surfaces(parts, pieces, ground)
    out = []
    for a, b in zip(tour, tour[1:]):
        if not path(stand, covered, (a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"]), ground):
            out.append(f"no walk from {a['name']} ({a['x']}, {a['y']}, {a['z']}) "
                       f"to {b['name']} ({b['x']}, {b['y']}, {b['z']})")
    return out
