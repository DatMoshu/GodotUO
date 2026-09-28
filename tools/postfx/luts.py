r"""Generate the built-in colour-grading LUTs for the post-processing stack (ADR-0023).

    python tools\postfx\luts.py            writes godot\GUO\postfx\luts\*.png

A LUT is a 16x16x16 colour cube as a 256x16 strip: blue picks the 16-pixel
tile, red the column inside it, green the row (the layout lut.gdshader reads).
Each look is a function of an (r, g, b) in 0..1, so the PNGs are reproducible
from this file; edit a function and re-run to change a look.
"""

from __future__ import annotations

import colorsys
from pathlib import Path

N = 16
OUT = Path(__file__).resolve().parents[2] / "godot" / "GUO" / "postfx" / "luts"


def clamp(v: float) -> float:
    return 0.0 if v < 0 else 1.0 if v > 1 else v


def neutral(r, g, b):
    return r, g, b


def teal_orange(r, g, b):
    # Warm skin and fire, teal shadows: the blockbuster split-tone.
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    sh = 1 - lum
    return (clamp(r + 0.10 * lum - 0.06 * sh), clamp(g + 0.02 * lum + 0.02 * sh), clamp(b - 0.10 * lum + 0.10 * sh))


def faded_film(r, g, b):
    # Lifted blacks, softened whites, a little less saturation, warm cast.
    h, l, s = colorsys.rgb_to_hls(r, g, b)
    r2, g2, b2 = colorsys.hls_to_rgb(h, l, s * 0.78)
    f = lambda v: 0.08 + v * 0.84  # noqa: E731
    return clamp(f(r2) + 0.02), clamp(f(g2)), clamp(f(b2) - 0.02)


def moonlight(r, g, b):
    # Night: blue-shifted, desaturated, darker mids.
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    m = lum ** 1.25
    return clamp(m * 0.75 + r * 0.1), clamp(m * 0.85 + g * 0.1), clamp(m * 1.05 + b * 0.15 + 0.03)


def emerald(r, g, b):
    # A lush, green-gold "fantasy" grade.
    h, l, s = colorsys.rgb_to_hls(r, g, b)
    r2, g2, b2 = colorsys.hls_to_rgb(h, l, min(1.0, s * 1.2))
    return clamp(r2 * 0.97 + 0.02), clamp(g2 * 1.05 + 0.01), clamp(b2 * 0.9)


LOOKS = {"neutral": neutral, "teal_orange": teal_orange, "faded_film": faded_film,
         "moonlight": moonlight, "emerald": emerald}


def main() -> int:
    from PIL import Image

    OUT.mkdir(parents=True, exist_ok=True)
    for name, fn in LOOKS.items():
        im = Image.new("RGB", (N * N, N))
        px = im.load()
        for bi in range(N):
            for gi in range(N):
                for ri in range(N):
                    r, g, b = fn(ri / (N - 1), gi / (N - 1), bi / (N - 1))
                    px[bi * N + ri, gi] = (round(r * 255), round(g * 255), round(b * 255))
        im.save(OUT / f"{name}.png")
        print(f"[postfx] {OUT / (name + '.png')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
