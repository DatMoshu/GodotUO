"""How two GUO variants' screenshots differ: every pixel that differs in any channel, and the isolated ones apart.

The count is exact: a pixel differs when any of R, G or B differs by one or
more. (Converting the RGB difference to luminance first would lose a
one-step change in a single channel, blue in particular.)

An isolated pixel is a differing pixel none of whose eight neighbours
differs. That describes the shape of the difference, not its cause and not
whether it is acceptable: it is reported beside the exact count so a reader
knows whether a difference is a patch (a sprite, a label, a radar dot, a
tile) or scattered single pixels, and then looks at the diff picture.
"""
from __future__ import annotations

from PIL import Image, ImageChops


def differing(a: Image.Image, b: Image.Image) -> Image.Image:
    """A mode-L mask: 255 where the two RGB images differ in any channel."""
    delta = ImageChops.difference(a.convert("RGB"), b.convert("RGB"))
    red, green, blue = delta.split()
    any_channel = ImageChops.lighter(ImageChops.lighter(red, green), blue)
    return any_channel.point(lambda v: 255 if v > 0 else 0)


def count(mask: Image.Image) -> tuple[int, int, list[tuple[int, int]]]:
    """(all differing pixels, the isolated ones among them, where the isolated ones are)."""
    w, h = mask.size
    data = mask.tobytes()
    total = sum(1 for v in data if v)
    if not total:
        return 0, 0, []
    isolated = []
    for i, v in enumerate(data):
        if not v:
            continue
        x, y = i % w, i // w
        alone = True
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if (dx or dy) and 0 <= x + dx < w and 0 <= y + dy < h and data[(y + dy) * w + x + dx]:
                    alone = False
                    break
            if not alone:
                break
        if alone:
            isolated.append((x, y))
    return total, len(isolated), isolated


def describe(total: int, isolated: int) -> str:
    """The exact count, with the isolated count beside it for information."""
    if not total:
        return "0 px"
    return f"{total} px" if not isolated else f"{total} px ({isolated} isolated)"
