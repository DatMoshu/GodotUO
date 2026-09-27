#!/usr/bin/env python3
"""Rasterise the project icon (godot/GUO/icon.svg) into the PNGs an Android
launcher wants, so the app on the device carries GUO's icon and not Godot's.

Godot's Android export takes PNGs only, and the machine has no SVG
rasteriser, so the icon's shapes -- a rounded slab and a wireframe
isometric block -- are drawn again here with Pillow, supersampled.
Keep this in step with icon.svg when that changes.

Writes into godot/GUO/android_icons/:
    main_192.png        legacy launcher icon (slab + block)
    foreground_432.png  adaptive foreground (block only, in the safe zone)
    background_432.png  adaptive background (the slab colour, edge to edge)
    monochrome_432.png  adaptive monochrome (white block on transparent)

Usage:
    python tools\\android\\icons.py
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw

SLAB = (0x1B, 0x24, 0x30, 255)
LINE = (0x6F, 0xD3, 0xC7, 255)
WHITE = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)

# icon.svg, in its own 128x128 units.
SVG_SIZE = 128
SVG_RADIUS = 16
SVG_STROKE = 3
# The three stroked paths of the block, as polylines.
BLOCK = [
    [(64, 30), (104, 54), (64, 78), (24, 54), (64, 30)],
    [(24, 54), (24, 74), (64, 98), (104, 74), (104, 54)],
    [(64, 78), (64, 98)],
]

SS = 4  # supersampling factor


def _draw_block(draw: ImageDraw.ImageDraw, scale: float, offset: float, colour) -> None:
    width = max(1, round(SVG_STROKE * scale))
    for path in BLOCK:
        pts = [(x * scale + offset, y * scale + offset) for x, y in path]
        draw.line(pts, fill=colour, width=width, joint="curve")
        # Round caps, as the SVG's default butt caps read thin at small sizes.
        r = width / 2
        for x, y in pts:
            draw.ellipse([x - r, y - r, x + r, y + r], fill=colour)


def _finish(img: Image.Image, size: int) -> Image.Image:
    return img.resize((size, size), Image.LANCZOS)


def main_icon(size: int) -> Image.Image:
    big = size * SS
    img = Image.new("RGBA", (big, big), CLEAR)
    d = ImageDraw.Draw(img)
    scale = big / SVG_SIZE
    d.rounded_rectangle([0, 0, big - 1, big - 1], radius=SVG_RADIUS * scale, fill=SLAB)
    _draw_block(d, scale, 0, LINE)
    return _finish(img, size)


def foreground(size: int, colour) -> Image.Image:
    """The block alone, fitted to the adaptive icon's inner 66% safe zone."""
    big = size * SS
    img = Image.new("RGBA", (big, big), CLEAR)
    d = ImageDraw.Draw(img)
    safe = big * 0.66
    scale = safe / SVG_SIZE
    offset = (big - safe) / 2
    _draw_block(d, scale, offset, colour)
    return _finish(img, size)


def background(size: int) -> Image.Image:
    return Image.new("RGBA", (size, size), SLAB)


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    out = root / "godot" / "GUO" / "android_icons"
    out.mkdir(parents=True, exist_ok=True)
    main_icon(192).save(out / "main_192.png")
    foreground(432, LINE).save(out / "foreground_432.png")
    background(432).save(out / "background_432.png")
    foreground(432, WHITE).save(out / "monochrome_432.png")
    for f in sorted(out.glob("*.png")):
        print(f"[android] wrote {f.relative_to(root)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
