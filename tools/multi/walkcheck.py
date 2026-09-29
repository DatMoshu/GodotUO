"""Can a walker get from each tour stop to the next? An offline check of a built scene.

A rough model of UO movement, enough to catch a stair that lands behind a parapet or a floor
that is missing before a proof spends twenty minutes finding it:
- a cell can be stood on at the top of a surface piece (half its height for a bridge, as the
  client counts stairs), when nothing impassable stands in the 16 z above it and no solid
  surface (a stair's block, a step) starts there: a floor under a stair's stacked blocks is
  buried, though its own flags would let a walker stand on it;
- a walker moves to any of the eight neighbours, up at most a step (5 z; a stair's pieces
  rise 5 at a time), or down any distance (a walker can drop off a ledge) onto the highest
  surface in reach, never through a floor, and not into a cell whose blocks stand in the way
  between the two heights (the side of a stair's flight); a diagonal step
  also needs both cells beside it open at about that height (UO does not cut corners);
- the land counts as a surface at the scene's ground wherever nothing covers it (one height, or
  the map's own per cell);
- nothing above z 112 is stood on: the client's pathfinder caps every cell at 128.
The shard and the client are the proof; this only says where to look.
"""
from __future__ import annotations

import heapq

HEADROOM = 16
# the client's pathfinder puts a ceiling at z 128 over every cell (CalculateNewZ) and wants a walker's
# 16 under it: nothing above z 112 can be stood on, whatever the shard allows
MAX_STAND = 128 - HEADROOM
BOTTOMS: dict = {}
CLIMB = 5
# a leg longer than this many times its straight distance (plus the slack) is a detour the client's
# pathfinder may run out of nodes on: a culvert too low to pass sent it round a wall's far end, far
# longer than the six cells through, and it gave up. The bound is a guess, not a measured limit
DETOUR, DETOUR_SLACK = 3, 30
# a leg longer than this (in cells, the larger of dx and dy) can reach past the map the client has
# loaded round the player, and it answers "no path": seen at 20 cells, after the walk had come from
# the far side (the same leg passed when the client had been at the goal before)
LEG_MAX = 18


def land_at(ground, at) -> int:
    """The land's height at a cell: `ground` is one height for the whole scene, or {(x, y): z}
    read from the map (0 where it has none)."""
    return ground.get(at, 0) if isinstance(ground, dict) else ground


def surfaces(parts: list[dict], pieces: dict, ground=0, land_under: bool = True) -> tuple[dict, set]:
    """{(x, y): [standing z, ...]} and the set of cells anything stands in."""
    stand: dict[tuple, set] = {}
    solid: dict[tuple, list] = {}
    above: dict[tuple, list] = {}      # bottoms of solid surfaces (a block, a step): nobody stands in one
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
                if h:
                    above.setdefault(at, []).append(c.z)
            if "impassable" in flags:
                solid.setdefault(at, []).append((c.z, c.z + h))
    out = {}
    # the bottoms of whatever a walker cannot pass through, per cell: search() keeps a drop out of
    # a cell whose pieces stand between the height it leaves and the one it lands on
    global BOTTOMS
    BOTTOMS = {at: [a for a, _ in solid.get(at, ())] + above.get(at, []) for at in covered}
    for at in set(stand) | covered:
        zs = set(stand.get(at, ()))
        g = land_at(ground, at)
        # land_under False: the land over the parts is not walked on (the void's land is
        # impassable; a scene sunk below it is entered by its own stair)
        if land_under and not any(a <= g < b for a, b in solid.get(at, ())):
            zs.add(g)
        free = sorted(z for z in zs if z <= MAX_STAND and not any(a < z + HEADROOM and b > z for a, b in solid.get(at, ()))
                      and not any(z <= a < z + HEADROOM for a in above.get(at, ())))
        if free:
            out[at] = free
    return out, covered


def path(stand: dict, covered: set, start: tuple, goal: tuple, ground=0, slack: int = 4) -> int | None:
    """The steps of the shortest walk from (x, y, z) to (x, y, z within slack), over the scene and
    the open land round it; None when there is none."""
    got = search(stand, covered, start, goal, ground, slack)
    return None if got is None else got[0]


def search(stand: dict, covered: set, start: tuple, goal: tuple, ground=0,
           slack: int | None = 4) -> tuple[int, int] | None:
    """(steps, z reached) of the shortest walk to the goal's x and y, at its z within slack, or at
    any z with slack None (where the client's pathfinder stops: it aims at x and y only)."""
    def spots(at):
        if at in stand:
            return stand[at]
        return [land_at(ground, at)] if at not in covered else []

    sz = min(spots(start[:2]), key=lambda z: abs(z - start[2]), default=None)
    if sz is None:
        return None
    first = (start[0], start[1], sz)
    seen = {first: 0}
    heap = [(0, 0, first)]
    # the scene and the land round it, and both ends of the leg wherever they are
    xs = [x for x, _ in covered] + [start[0], goal[0]]
    ys = [y for _, y in covered] + [start[1], goal[1]]
    lo_x, hi_x, lo_y, hi_y = min(xs) - 4, max(xs) + 4, min(ys) - 4, max(ys) + 4
    while heap:
        _, g, (x, y, z) = heapq.heappop(heap)
        if g > seen[(x, y, z)]:
            continue
        if (x, y) == goal[:2] and (slack is None or abs(z - goal[2]) <= slack):
            return g, z
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                n = (x + dx, y + dy)
                if (dx, dy) == (0, 0) or not (lo_x <= n[0] <= hi_x and lo_y <= n[1] <= hi_y):
                    continue
                # a step lands on the highest surface it can reach, never through a floor to one below
                for nz in [max((v for v in spots(n) if v - z <= CLIMB), default=None)]:
                    if nz is None or any(nz <= a < max(z, nz) + HEADROOM for a in BOTTOMS.get(n, ())):
                        continue
                    if dx and dy and not all(any(abs(sz - max(z, nz)) <= CLIMB or min(z, nz) <= sz <= max(z, nz)
                                                 for sz in spots(side))
                                             for side in ((x + dx, y), (x, y + dy))):
                        continue
                    k = (n[0], n[1], nz)
                    if g + 1 < seen.get(k, 1 << 30):
                        seen[k] = g + 1
                        est = max(abs(n[0] - goal[0]), abs(n[1] - goal[1]))
                        heapq.heappush(heap, (g + 1 + est, g + 1, k))
    return None


def check_tour(parts: list[dict], tour: list[dict], pieces: dict, ground=0, land_under: bool = True) -> list[str]:
    """Each leg of the tour that has no walk, as a problem line."""
    stand, covered = surfaces(parts, pieces, ground, land_under)
    out = []
    for a, b in zip(tour, tour[1:]):
        steps = path(stand, covered, (a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"]), ground)
        leg = f"from {a['name']} ({a['x']}, {a['y']}, {a['z']}) to {b['name']} ({b['x']}, {b['y']}, {b['z']})"
        if max(abs(a["x"] - b["x"]), abs(a["y"] - b["y"])) > LEG_MAX:
            out.append(f"the leg {leg} is over {LEG_MAX} cells: the client may not have that far loaded; put a stop on the way")
        elif steps is None:
            out.append(f"no walk {leg}")
        elif ((near := search(stand, covered, (a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"]), ground, None))
              and abs(near[1] - b["z"]) > 4 and near[0] < steps):
            out.append(f"the walk {leg} stops short at z {near[1]}: the client's pathfinder aims at x and y only, "
                       f"and that z is nearer; put a stop on the way (the middle of the stair)")
        elif steps > DETOUR * max(abs(a["x"] - b["x"]), abs(a["y"] - b["y"])) + DETOUR_SLACK:
            out.append(f"the walk {leg} is a detour of {steps} steps: the client's pathfinder (A* on the "
                       "straight distance, 10000 nodes) gives up on such; put a stop on the way")
    return out
