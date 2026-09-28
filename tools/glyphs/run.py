"""Build GUO's input glyphs from Kenney's "Input Prompts Pixel" (CC0).

The glyphs the client draws for its button prompts (src/Input/Glyphs) are a
small subset of Kenney's 16x16 pack, copied by tile number and given names by
meaning, plus the four PlayStation symbols, which the pixel pack does not
draw: they are drawn here onto the pack's own blank light disc (tile 12), in
the pack's outline grey. The output is committed; this only needs running to
change the set.

    python tools/glyphs/run.py --source "<Input Prompts Pixel folder>"

--source is the folder holding Tilemap/tilemap_packed.png and License.txt
(Kenney's "Input Prompts Pixel" 1.0, www.kenney.nl). It is never assumed:
pass it, or set KENNEY_INPUT_PROMPTS.
"""
from __future__ import annotations

import argparse
import os
import shutil
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "godot" / "GUO" / "src" / "Resources" / "embedded" / "glyphs"
TILE = 16
PER_ROW = 34

# name -> tile number in tilemap_packed.png
TILES = {
    # Face buttons by printed letter (Xbox, Nintendo, Steam Deck, any lettered pad),
    # the light neutral set: they sit on stone like the client's marble plates.
    "pad_a": 13,
    "pad_b": 14,
    "pad_x": 15,
    "pad_y": 16,
    "pad_back": 616,  # the View button (two overlapping squares): Back / Select
    "dpad": 34,
    "stick_l": 416,
    "stick_r": 484,
    "key_esc": 17,
    "mouse": 76,
    "mouse_left": 77,
    "mouse_right": 78,
}

DISC = 12  # the blank light disc the PlayStation symbols are drawn on

# The PlayStation symbols, 6x6 where the pack letters its buttons (x 5-10,
# y 4-9 of the disc), "#" = ink.
SYMBOLS = {
    "ps_cross": [
        "#....#",
        ".#..#.",
        "..##..",
        "..##..",
        ".#..#.",
        "#....#",
    ],
    "ps_circle": [
        ".####.",
        "#....#",
        "#....#",
        "#....#",
        "#....#",
        ".####.",
    ],
    "ps_square": [
        "######",
        "#....#",
        "#....#",
        "#....#",
        "#....#",
        "######",
    ],
    "ps_triangle": [
        "..##..",
        "..##..",
        ".#..#.",
        ".#..#.",
        "#....#",
        "######",
    ],
}


def tile(sheet: Image.Image, n: int) -> Image.Image:
    r, c = divmod(n, PER_ROW)
    return sheet.crop((c * TILE, r * TILE, c * TILE + TILE, r * TILE + TILE))


def ink_of(sheet: Image.Image) -> tuple:
    """The grey the pack letters its light buttons in: the commonest colour of
    tile 13's A where it differs from the blank disc (the rest is highlight)."""
    a, disc = tile(sheet, 13), tile(sheet, DISC)
    seen = Counter(a.getpixel((x, y)) for y in range(TILE) for x in range(TILE)
                   if a.getpixel((x, y)) != disc.getpixel((x, y)))
    if not seen:
        raise SystemExit("[glyphs] could not find the letter grey in tile 13")
    return seen.most_common(1)[0][0]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--source", default=os.environ.get("KENNEY_INPUT_PROMPTS"))
    args = ap.parse_args()

    if not args.source:
        print("[glyphs] pass --source (Kenney's Input Prompts Pixel folder) or set KENNEY_INPUT_PROMPTS")
        return 2

    src = Path(args.source)
    sheet_path = src / "Tilemap" / "tilemap_packed.png"
    if not sheet_path.is_file():
        print(f"[glyphs] no Tilemap/tilemap_packed.png under {src}")
        return 2

    sheet = Image.open(sheet_path).convert("RGBA")
    OUT.mkdir(parents=True, exist_ok=True)

    for name, n in TILES.items():
        tile(sheet, n).save(OUT / f"{name}.png")

    ink = ink_of(sheet)
    for name, rows in SYMBOLS.items():
        img = tile(sheet, DISC).copy()
        for y, row in enumerate(rows):
            for x, ch in enumerate(row):
                if ch == "#":
                    img.putpixel((5 + x, 4 + y), ink)
        img.save(OUT / f"{name}.png")

    shutil.copyfile(src / "License.txt", OUT / "License.txt")
    print(f"[glyphs] {len(TILES) + len(SYMBOLS)} glyphs -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
