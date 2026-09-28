"""Checks a generated multi before it is written.

  ids        every item exists in tiledata and has art
  closed     with the doors shut, the outside reaches no floor cell (no gap in a wall)
  reachable  with the doors open, every floor cell of the ground storey is reached from outside,
             and every upper storey from a stair
  z          every z fits the record (int16) and the client's map range (-128..127)
  size       the shard reads at most MAX_COMPONENTS components per multi
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from guo.uoread import Art, TileData  # noqa: E402
from multifile import MAX_COMPONENTS, Component  # noqa: E402


def flood(start: set, passable, limit: tuple[int, int, int, int]) -> set:
    x0, y0, x1, y1 = limit
    seen, stack = set(start), list(start)
    while stack:
        x, y = stack.pop()
        for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if n not in seen and x0 <= n[0] <= x1 and y0 <= n[1] <= y1 and passable(n):
                seen.add(n)
                stack.append(n)
    return seen


def check_storey(st: dict, n: int, entries: set | None) -> list[str]:
    """entries: None for the ground storey (entered from outside), else the cells a stair arrives on."""
    walls, doors, floor = set(map(tuple, st["walls"])), set(map(tuple, st["doors"])), set(map(tuple, st["floor"]))
    xs = [c[0] for c in walls | floor]
    ys = [c[1] for c in walls | floor]
    lim = (min(xs) - 1, min(ys) - 1, max(xs) + 1, max(ys) + 1)
    outside = {(x, y) for x in range(lim[0], lim[2] + 1) for y in (lim[1], lim[3])} | \
              {(x, y) for y in range(lim[1], lim[3] + 1) for x in (lim[0], lim[2])}
    problems = []
    walk_floor = floor - walls
    shut = flood(outside, lambda c: c not in walls and c not in doors, lim)
    leaks = sorted(walk_floor & shut)
    if n == 0 and leaks:
        problems.append(f"storey {n}: walls not closed, outside reaches {leaks[:6]}")
    if entries is None:
        reached = flood(outside, lambda c: c not in walls, lim)
    else:
        reached = flood(set(entries) & walk_floor, lambda c: c in walk_floor or c in doors, lim)
    missing = sorted(walk_floor - reached)
    if missing:
        problems.append(f"storey {n}: {len(missing)} floor cells unreachable, e.g. {missing[:6]}")
    return problems


def validate(comps: list[Component], side: dict, data_dir: Path | None) -> list[str]:
    """Problems found, or []. With no data_dir the item ids are not checked (the synthetic tests)."""
    problems = []
    if data_dir is not None:
        td, art = TileData(data_dir), Art(data_dir)
        for item in sorted({c.item for c in comps}):
            if td.static(item) is None:
                problems.append(f"item {item:#06x} is not in tiledata")
            elif art.get_static(item) is None and any(c.visible for c in comps if c.item == item):
                problems.append(f"item {item:#06x} has no art")
    for c in comps:
        if not -128 <= c.z <= 127:
            problems.append(f"z {c.z} out of range at {c.x},{c.y} ({c.item:#06x})")
            break
    if len(comps) > MAX_COMPONENTS:
        problems.append(f"{len(comps)} components, the shard reads at most {MAX_COMPONENTS}")
    storeys = side["local"]["storeys"]
    for n, st in enumerate(storeys):
        entries = None if n == 0 else {tuple(c) for c in st.get("arrivals", [])}
        if n > 0 and not entries:
            problems.append(f"storey {n}: no stair arrives on it")
            continue
        problems += check_storey(st, n, entries)
    if not side["doors"]:
        problems.append("no door")
    return problems
