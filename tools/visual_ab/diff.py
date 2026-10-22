"""How two GUO variants' screenshots differ: every pixel, and the isolated ones apart.

An isolated pixel is a differing pixel none of whose eight neighbours differs.
A moved sprite, a label, a radar dot or a missing tile differs in a patch; a
single texel is what the land-array path's nearest sampling gives on a
stretched tile when the rescaled UV lands on the other side of a texel edge
(PERF-V7, docs/perf/2026-10-19_cover_regression.md, "The river scenes").
Reported apart so a run can tell the two kinds without a human at the sheet.
"""
from __future__ import annotations

from PIL import Image, ImageChops


def differing(a: Image.Image, b: Image.Image) -> Image.Image:
    """A mode-L mask: 255 where the two RGB images differ in any channel."""
    return ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L").point(lambda v: 255 if v > 0 else 0)


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
    if not total:
        return "0 px"
    return f"{total} px" if not isolated else f"{total} px ({isolated} isolated)"
