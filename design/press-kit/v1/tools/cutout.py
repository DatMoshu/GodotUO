"""Cut the Godot mark's black-background master out onto transparency.

    python tools/cutout.py

Writes masters/guo-godot-mark-transparent-native.png. The emblem's transparent
master, masters/guo-emblem-transparent-native.png, was cut separately and is
cleaner than this keyer manages on it -- it opens the diamond above the face,
which is too small for the area rule below -- so it is kept as delivered.
build.cjs builds every export that should float on any background from the two.

The generator could not return real alpha (see PROMPTS.md), so the masters
were delivered on pure black. Keying that black is exact rather than a guess
wherever an edge pixel is metal blended with black: alpha is how far the pixel
is from black, and the colour is un-blended from it. That is done only in a
thin band around the black areas, so dark engraving inside the metal stays
opaque. Black areas count as background when they are large -- the outside and
the gaps enclosed between the ring and the face -- not just when they touch the
canvas edge.
"""

from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parents[1]
MARKS = ["guo-godot-mark"]

BLACK = 4         # a channel max at or below this is background black
MIN_AREA = 3000   # enclosed black smaller than this is shading inside the art
BAND = 3          # px of soft edge keyed around the background


def cut(src: Path) -> Image.Image:
    rgb = np.asarray(Image.open(src).convert("RGB")).astype(np.float32)
    v = rgb.max(axis=2)

    dark = v <= BLACK
    labels, n = ndimage.label(dark)
    areas = ndimage.sum(dark, labels, index=np.arange(1, n + 1))
    background = np.isin(labels, np.flatnonzero(areas >= MIN_AREA) + 1)

    alpha = np.ones_like(v)
    alpha[background] = 0.0

    band = ndimage.binary_dilation(background, iterations=BAND) & ~background
    alpha[band] = np.clip(v[band] / 255.0 * 2.0, 0.0, 1.0)

    # Un-blend from black: an edge pixel is alpha * colour, so colour is the
    # pixel over alpha. Alpha rises twice as fast as brightness, so a faint
    # glow keeps some body instead of fading out completely.
    out = np.zeros(rgb.shape[:2] + (4,), np.float32)
    colour = np.clip(rgb / np.maximum(alpha, 1e-6)[..., None], 0, 255)
    out[..., :3] = np.where(band[..., None], colour, rgb)
    out[..., 3] = alpha * 255.0
    out[alpha == 0] = 0
    return Image.fromarray(out.round().astype(np.uint8), "RGBA")


def main() -> None:
    for mark in MARKS:
        dest = ROOT / "masters" / f"{mark}-transparent-native.png"
        cut(ROOT / "masters" / f"{mark}-native.png").save(dest, optimize=True)
        print(f"wrote {dest.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
