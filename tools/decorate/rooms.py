"""Rooms on one storey's grid: floor regions bounded by walls, split at doors.

Pure geometry on sets of (x, y) cells, shared by the miner (buildings read
from the statics or a client multi) and the decorator (a built multi's
sidecar). No client data here, so the synthetic test can drive it.

The grid follows the client's houses: a wall stands on a cell; the floor can
run under the south and east walls, so the cells a room owns are its floor
cells minus its wall cells.
"""
from __future__ import annotations

from dataclasses import dataclass, field

DIRS = (("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0))
STEP = {d: (dx, dy) for d, dx, dy in DIRS}
OPPOSITE = {"N": "S", "S": "N", "E": "W", "W": "E"}
Cell = tuple[int, int]


@dataclass
class Room:
    cells: set[Cell]
    doors: list[tuple[Cell, str]] = field(default_factory=list)      # the door cell, and the side of the room it is on
    windows: list[tuple[Cell, str]] = field(default_factory=list)
    open_edges: int = 0                                              # boundary steps that lead neither to a wall nor a door
    wall_edges: int = 0

    @property
    def bbox(self) -> tuple[int, int, int, int]:
        xs, ys = [c[0] for c in self.cells], [c[1] for c in self.cells]
        return min(xs), min(ys), max(xs), max(ys)

    @property
    def size(self) -> tuple[int, int]:
        x0, y0, x1, y1 = self.bbox
        return x1 - x0 + 1, y1 - y0 + 1

    @property
    def fill(self) -> float:
        w, h = self.size
        return len(self.cells) / (w * h)

    @property
    def shape(self) -> str:
        f = self.fill
        return "rect" if f >= 0.999 else "near-rect" if f >= 0.85 else "L-or-irregular"

    @property
    def enclosed(self) -> bool:
        edges = self.open_edges + self.wall_edges + len(self.doors)
        return edges > 0 and self.open_edges <= 0.1 * edges


def door_gaps(floor: set[Cell], walls: set[Cell]) -> set[Cell]:
    """Cells in a wall line with no wall on them: a floor (or bare) cell whose N and S
    neighbours are walls and whose E and W are not, or the other way round. A run of
    more than two in a row is a corridor, not a door."""
    def closed(x, y, dx, dy):
        # a wall one or two steps away on both sides along this axis (a door up to two wide)
        return (((x - dx, y - dy) in walls or (x - 2 * dx, y - 2 * dy) in walls)
                and ((x + dx, y + dy) in walls or (x + 2 * dx, y + 2 * dy) in walls))

    cand = set()
    for (x, y) in floor:
        if (x, y) in walls:
            continue
        side_x = (x - 1, y) in walls or (x + 1, y) in walls
        side_y = (x, y - 1) in walls or (x, y + 1) in walls
        if closed(x, y, 1, 0) and not side_y:
            cand.add((x, y))              # in a wall running along x
        elif closed(x, y, 0, 1) and not side_x:
            cand.add((x, y))              # in a wall running along y
    out = set()
    seen = set()
    for c in cand:
        if c in seen:
            continue
        group, stack = [], [c]
        seen.add(c)
        while stack:
            g = stack.pop()
            group.append(g)
            for _d, dx, dy in DIRS:
                n = (g[0] + dx, g[1] + dy)
                if n in cand and n not in seen:
                    seen.add(n)
                    stack.append(n)
        if len(group) <= 2:
            out |= set(group)
    return out


def components(cells: set[Cell]) -> list[set[Cell]]:
    """4-connected components, in a stable order (by their smallest cell)."""
    left, out = set(cells), []
    for start in sorted(cells):
        if start not in left:
            continue
        comp, stack = set(), [start]
        left.discard(start)
        while stack:
            c = stack.pop()
            comp.add(c)
            for _d, dx, dy in DIRS:
                n = (c[0] + dx, c[1] + dy)
                if n in left:
                    left.discard(n)
                    stack.append(n)
        out.append(comp)
    return out


def segment(floor: set[Cell], walls: set[Cell], doors: set[Cell] | None = None,
            windows: set[Cell] | None = None, blocked: set[Cell] | None = None,
            min_cells: int = 4) -> list[Room]:
    """Rooms: the floor minus walls and door cells, cut into 4-connected regions.

    `doors` are known door cells (a sidecar's); when None they are found as gaps in
    wall lines. `blocked` cells (stairs, landings) are left out of every room but do
    not bound it as walls do."""
    windows = windows or set()
    blocked = blocked or set()
    if doors is None:
        doors = door_gaps(floor | _gaps_between(walls), walls)
    inner = floor - walls - doors - blocked
    rooms = []
    for comp in components(inner):
        if len(comp) < min_cells:
            continue
        r = Room(comp)
        seen_doors, seen_windows = set(), set()
        for (x, y) in comp:
            for d, dx, dy in DIRS:
                n = (x + dx, y + dy)
                if n in comp or n in blocked:
                    continue
                if n in doors:
                    if n not in seen_doors:
                        seen_doors.add(n)
                        r.doors.append((n, d))
                elif n in walls:
                    r.wall_edges += 1
                    if n in windows and n not in seen_windows:
                        seen_windows.add(n)
                        r.windows.append((n, d))
                else:
                    r.open_edges += 1
        r.doors.sort()
        r.windows.sort()
        rooms.append(r)
    return rooms


def _gaps_between(walls: set[Cell]) -> set[Cell]:
    """Bare cells with walls on two opposite sides: where a door stands in a building
    whose door cells carry no floor tile (the originals leave the door cell empty)."""
    out = set()
    xs = {x for x, _ in walls}
    if not xs:
        return out
    for (x, y) in walls:
        for dx, dy in ((1, 0), (0, 1)):
            for k in (2, 3):
                far = (x + dx * k, y + dy * k)
                if far in walls:
                    for j in range(1, k):
                        c = (x + dx * j, y + dy * j)
                        if c not in walls:
                            out.add(c)
                    break
    return out


def wall_sides(cell: Cell, walls: set[Cell]) -> str:
    """Which of N/E/S/W hold a wall next to a cell, in NESW order ('' in the open)."""
    return "".join(d for d, dx, dy in DIRS if (cell[0] + dx, cell[1] + dy) in walls)


def reachable(start: Cell, free: set[Cell]) -> set[Cell]:
    if start not in free:
        return set()
    seen, stack = {start}, [start]
    while stack:
        c = stack.pop()
        for _d, dx, dy in DIRS:
            n = (c[0] + dx, c[1] + dy)
            if n in free and n not in seen:
                seen.add(n)
                stack.append(n)
    return seen


def shortest_path(a: Cell, b: Cell, free: set[Cell]) -> list[Cell]:
    """Breadth first over `free` (both ends must be in it); [] when there is no path."""
    if a not in free or b not in free:
        return []
    prev = {a: None}
    queue = [a]
    while queue:
        nq = []
        for c in queue:
            if c == b:
                out = []
                while c is not None:
                    out.append(c)
                    c = prev[c]
                return out[::-1]
            for _d, dx, dy in DIRS:
                n = (c[0] + dx, c[1] + dy)
                if n in free and n not in prev:
                    prev[n] = c
                    nq.append(n)
        queue = nq
    return []
