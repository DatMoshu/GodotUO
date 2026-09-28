"""Piece roles, materials and facings, from tiledata flags and placement only.

A role is what a tile does in a building: wall, window, door, floor, stair,
roof, and everything else is furnishing ("deco"). Invisible components are
"marker" (door, sign and centre spots that the shard fills with real items).
The material is the tile name with the role word removed. Facing is measured
where the tile is used (see facing_of), never assumed from its id.
"""
from __future__ import annotations

import collections

# TileFlag bits (TileDataLoader / ModernUO TileFlag)
BACKGROUND, WALL, IMPASSABLE, SURFACE, BRIDGE = 0x1, 0x10, 0x40, 0x200, 0x400
WINDOW, ROOF, DOOR, STAIR_BACK, STAIR_RIGHT = 0x1000, 0x10000000, 0x20000000, 0x40000000, 0x80000000

FLAG_NAMES = {BACKGROUND: "background", WALL: "wall", IMPASSABLE: "impassable", SURFACE: "surface",
              BRIDGE: "bridge", WINDOW: "window", ROOF: "roof", DOOR: "door",
              STAIR_BACK: "stair_back", STAIR_RIGHT: "stair_right"}

ROLE_WORDS = ("wall", "walls", "roof", "roofing", "floor", "stairs", "stair", "steps", "door", "window",
              "post", "corner", "arch", "boards", "pavers", "tiles")
STRUCTURAL = {"wall", "window", "door", "floor", "stair", "roof", "post"}


def flag_names(flags: int) -> list[str]:
    return [n for b, n in FLAG_NAMES.items() if flags & b]


def role(tile: dict, visible: bool = True) -> str:
    if not visible:
        return "marker"
    f, name = tile["flags"], tile["name"].lower()
    if f & DOOR:
        return "door"
    if f & ROOF:
        return "roof"
    if f & (STAIR_BACK | STAIR_RIGHT) or ("stair" in name or "step" in name) and f & (SURFACE | BRIDGE):
        return "stair"
    if f & WINDOW or "window" in name and f & WALL:
        return "window"
    if f & WALL:
        return "post" if "post" in name or "pillar" in name else "wall"
    if f & SURFACE and tile["height"] == 0:
        return "floor"
    return "deco"


def material(tile: dict) -> str:
    words = [w for w in tile["name"].lower().split() if w not in ROLE_WORDS]
    return " ".join(words) or tile["name"].lower()


def facing_of(cells: set[tuple[int, int, int]], x: int, y: int, z: int) -> str:
    """How a wall tile at (x, y, z) runs, from same-z wall neighbours: 'x' (along x,
    a north/south-edge wall), 'y' (along y), 'corner' (both), 'post' (neither)."""
    along_x = (x - 1, y, z) in cells or (x + 1, y, z) in cells
    along_y = (x, y - 1, z) in cells or (x, y + 1, z) in cells
    return "corner" if along_x and along_y else "x" if along_x else "y" if along_y else "post"


def majority(counter: collections.Counter) -> str | None:
    return counter.most_common(1)[0][0] if counter else None
