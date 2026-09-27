#!/usr/bin/env python3
r"""Turn a sigil delivered on a *painted* checkerboard into one on real
transparency.

    python tools\brand\unkey.py <in.png> <out.png> [--tolerance 14] [--min-area 400]

The sigil arrived as guo-sigil-1254.png, "RGBA on a transparent
background"; its alpha was 255 everywhere and the transparency was a
checkerboard rendered into the pixels: roughly 16 px cells of two noisy
greys (about 130 and 191, plus or minus 8), irregular enough that no grid
fits them, so the key is by tone and texture rather than by position:

  1. every neutral pixel (channels within 10 of each other) whose luminance
     is within `tolerance` of either checker tone is a background candidate.
     The tones are the medians of the image's outer band;
  2. candidates are grouped into 8-connected components. A component is
     background if it touches the image border, or if it is at least
     `--min-area` pixels AND holds both tones (a fifth of it each): the
     ring's interior and the gap between the horns qualify. A patch of
     silver engraving does not: its gradients run through the gap between
     the two tones, which breaks it into small single-tone pieces;
  3. specks left inside the background (noise past the tolerance) smaller
     than 40 px are background too;
  4. background gets alpha 0, the rest 255 with a one-pixel feather along
     the cut. The icons are downscaled by 3x or more from here, which is
     where the real anti-aliasing happens.

It is a one-off: the clean master is what is committed
(design\brand\guo-sigil.png) and tools\brand\run.py refuses a master with
an opaque corner, so this cannot quietly happen again.
"""

from __future__ import annotations

import argparse
import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter


def outer_band(shape: tuple[int, int], border: int = 24) -> np.ndarray:
    h, w = shape
    band = np.zeros((h, w), dtype=bool)
    band[:border, :] = band[-border:, :] = band[:, :border] = band[:, -border:] = True
    return band


def checker_tones(gray: np.ndarray, neutral: np.ndarray) -> tuple[float, float]:
    """The light and dark tones of the checker: the outer band splits at its mean."""
    ring = gray[outer_band(gray.shape) & neutral]
    mid = ring.mean()
    return float(np.median(ring[ring >= mid])), float(np.median(ring[ring < mid]))


def components(mask: np.ndarray):
    """8-connected components of a boolean mask: labels, sizes, touches-border flags."""
    h, w = mask.shape
    labels = np.zeros((h, w), dtype=np.int32)
    sizes: list[int] = [0]
    border: list[bool] = [False]
    current = 0
    for sy in range(h):
        for sx in np.flatnonzero(mask[sy] & (labels[sy] == 0)):
            current += 1
            q = deque([(sy, int(sx))])
            labels[sy, sx] = current
            n = 0
            touches = False
            while q:
                y, x = q.popleft()
                n += 1
                if y == 0 or x == 0 or y == h - 1 or x == w - 1:
                    touches = True
                for dy in (-1, 0, 1):
                    yy = y + dy
                    if yy < 0 or yy >= h:
                        continue
                    for dx in (-1, 0, 1):
                        xx = x + dx
                        if xx < 0 or xx >= w or labels[yy, xx] or not mask[yy, xx]:
                            continue
                        labels[yy, xx] = current
                        q.append((yy, xx))
            sizes.append(n)
            border.append(touches)
    return labels, np.array(sizes), np.array(border)


def cleanup(background: np.ndarray, spread: np.ndarray) -> np.ndarray:
    """Close the seams and drop the stray cells.

    The seams between cells (lossy mid-greys, a pixel or two wide) form a
    lattice that no tone test catches: a morphological closing of the
    background (dilate 2 px, erode 2 px) swallows anything thinner than
    about 4 px, and the sigil's outline is far thicker than that. What is
    then left opaque is the sigil (one connected piece, by far the largest)
    plus cells whose tone drifted past the tolerance; any other opaque
    island that is neutral grey is such a cell.
    """
    m = Image.fromarray(np.where(background, 255, 0).astype(np.uint8), "L")
    background = np.asarray(m.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5))) > 0
    labels, sizes, _ = components(~background)
    main = int(np.argmax(sizes))
    spread_sum = np.bincount(labels.ravel(), weights=spread.ravel(), minlength=len(sizes))
    fill = (spread_sum / np.maximum(sizes, 1)) <= 12
    fill[0] = False
    fill[main] = False
    print(f"[unkey]   cleanup: {int(fill.sum())} stray grey islands filled, {len(sizes) - 2 - int(fill.sum())} coloured kept")
    return background | fill[labels]


def unkey(src: Path, dst: Path, tolerance: float, min_area: int) -> None:
    im = Image.open(src).convert("RGBA")
    rgb = np.asarray(im).astype(np.int16)[..., :3]
    gray = rgb.mean(axis=2)
    neutral = (rgb.max(axis=2) - rgb.min(axis=2)) <= 10
    h, w = gray.shape

    light, dark = checker_tones(gray, neutral)
    print(f"[unkey] {src.name}: {w}x{h}, checker tones light {light:.0f} dark {dark:.0f}, tolerance {tolerance}")

    is_light = neutral & (np.abs(gray - light) <= tolerance)
    is_dark = neutral & (np.abs(gray - dark) <= tolerance)
    candidate = is_light | is_dark
    labels, sizes, touches = components(candidate)
    light_count = np.bincount(labels[is_light], minlength=len(sizes))
    both = (light_count >= 0.2 * sizes) & (sizes - light_count >= 0.2 * sizes)
    keep = touches | ((sizes >= min_area) & both)
    keep[0] = False
    background = keep[labels]

    spread = (rgb.max(axis=2) - rgb.min(axis=2)).astype(np.float64)
    print(f"[unkey] {len(sizes) - 1} candidate components, {int(keep.sum())} taken as background")
    background = cleanup(background, spread)

    # Cells whose tone drifted past the tolerance and that touch the sigil
    # survive the above as part of it. A second, looser pass: neutral pixels
    # of any grey in the checker's whole range, within 24 px of the
    # background and connected to it, are background too. The sigil is
    # outlined in gold (saturated, so never neutral), which is what stops
    # this pass at its edge.
    near = np.asarray(Image.fromarray(np.where(background, 255, 0).astype(np.uint8), "L")
                      .filter(ImageFilter.MaxFilter(49))) > 0
    loose = near & ~background & neutral & (gray >= dark - 30) & (gray <= light + 30)
    l_labels, l_sizes, _ = components(loose)
    touching = np.asarray(Image.fromarray(np.where(background, 255, 0).astype(np.uint8), "L")
                          .filter(ImageFilter.MaxFilter(3))) > 0
    adjacent = np.zeros(len(l_sizes), dtype=bool)
    adjacent[np.unique(l_labels[touching & loose])] = True
    adjacent[0] = False
    grown = adjacent[l_labels]
    print(f"[unkey] fringe pass: {int(adjacent.sum())} grey patches on the sigil's edge, {int(grown.sum())} px")
    background = cleanup(background | grown, spread)
    print(f"[unkey] background {background.mean() * 100:.1f}% of the image")

    hard = np.where(background, 0, 255).astype(np.uint8)
    # The feather: a blurred copy of the hard mask, taken only where the hard
    # mask is opaque, so the sigil's outermost pixels fade and the background
    # stays exactly 0 (no grey fringe leaking outwards).
    soft = np.asarray(Image.fromarray(hard, "L").filter(ImageFilter.GaussianBlur(0.7)))
    final = np.where(hard == 255, soft, 0).astype(np.uint8)

    out = np.asarray(im).copy()
    out[..., 3] = final
    Image.fromarray(out, "RGBA").save(dst)
    print(f"[unkey] wrote {dst}; corner alpha {int(final[0, 0])}, bbox {Image.fromarray(out, 'RGBA').getbbox()}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("src", type=Path)
    ap.add_argument("dst", type=Path)
    ap.add_argument("--tolerance", type=float, default=14, help="how far from a checker tone a pixel may sit")
    ap.add_argument("--min-area", type=int, default=400, help="smallest enclosed checker region taken as background")
    args = ap.parse_args(argv)
    unkey(args.src, args.dst, args.tolerance, args.min_area)
    return 0


if __name__ == "__main__":
    sys.exit(main())
