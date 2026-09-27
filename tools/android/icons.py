#!/usr/bin/env python3
"""Build the Android launcher icons from the approved GUO emblem, so the
app on a device carries the brand and not Godot's robot.

The brand is the full GUO emblem: the horned ankh frame around the Godot
head, engraved silver and gold, with blue-white "Gems of Immortality"
eyes. Its master lives in the separate public repository (UO_Port_public,
design/press-kit/v2/guo-gems-of-immortality.png, 1254 px RGBA on
transparent); that repository's godot/GUO/icon.png and icon.ico are the
app icon derived from it, and are copied into this project by hand. This
tool builds only what Android wants, from the master:

    godot/GUO/android_icons/
        main_192.png        legacy launcher icon: the emblem on near-black
        foreground_432.png  adaptive foreground: the emblem scaled to sit
                            inside the inner 66% safe zone, centred
        background_432.png  adaptive background: flat near-black
        monochrome_432.png  adaptive monochrome: the emblem's alpha, white

Usage:
    python tools\\android\\icons.py [--brand <folder or png>]

The master is looked up in this order: --brand, then UO_ANDROID_BRAND_DIR
(environment, then launchers\\_shared\\config.bat). Either may name the
PNG itself or a folder holding it (v2/ under the press-kit folder is
searched). Only the four PNGs above are written; the master is not copied.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import load_config  # noqa: E402

MASTER = "guo-gems-of-immortality.png"

# Near-black rather than pure black: the emblem's own shading bottoms out
# around this, so its darkest engraving does not vanish into the slab.
BACKGROUND = (0x0A, 0x0A, 0x0C, 255)
WHITE = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)

LEGACY = 192
ADAPTIVE = 432
SAFE_ZONE = 0.66  # Android masks an adaptive icon to its inner 66%


def find_master(where: Path) -> Path:
    """The master PNG itself, or the newest press-kit version folder holding it."""
    if where.is_file():
        return where
    for c in [where, where / "v2"] + sorted(where.glob("v*"), reverse=True):
        if (c / MASTER).is_file():
            return c / MASTER
    sys.exit(f"[android] no {MASTER} at {where} (or a v*/ folder under it)")


def fitted(master: Image.Image, size: int, fraction: float) -> Image.Image:
    """The emblem's opaque extent scaled to `fraction` of `size`, centred on transparent."""
    bbox = master.getbbox()
    mark = master.crop(bbox) if bbox else master
    box = size * fraction
    scale = min(box / mark.width, box / mark.height)
    w, h = max(1, round(mark.width * scale)), max(1, round(mark.height * scale))
    out = Image.new("RGBA", (size, size), CLEAR)
    out.alpha_composite(mark.resize((w, h), Image.LANCZOS), ((size - w) // 2, (size - h) // 2))
    return out


def on_background(mark: Image.Image) -> Image.Image:
    out = Image.new("RGBA", mark.size, BACKGROUND)
    out.alpha_composite(mark)
    return out


def monochrome(foreground: Image.Image) -> Image.Image:
    """The emblem's alpha as white: Android tints it to the theme."""
    out = Image.new("RGBA", foreground.size, WHITE)
    out.putalpha(foreground.getchannel("A"))
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--brand", default=None, help=f"{MASTER}, or a folder holding it (v2/ is searched)")
    args = parser.parse_args(argv)

    cfg = load_config()
    where = Path(args.brand) if args.brand else cfg.android_brand_dir
    if where is None:
        sys.exit("[android] no brand master configured: pass --brand or set UO_ANDROID_BRAND_DIR in config.bat")
    master_path = find_master(where)
    print(f"[android] brand master: {master_path}")
    master = Image.open(master_path).convert("RGBA")

    root = cfg.root
    out = root / "godot" / "GUO" / "android_icons"
    out.mkdir(parents=True, exist_ok=True)

    # The legacy icon has no mask, so the emblem may fill most of the tile.
    on_background(fitted(master, LEGACY, 0.9)).save(out / "main_192.png")
    fg = fitted(master, ADAPTIVE, SAFE_ZONE)
    fg.save(out / "foreground_432.png")
    Image.new("RGBA", (ADAPTIVE, ADAPTIVE), BACKGROUND).save(out / "background_432.png")
    monochrome(fg).save(out / "monochrome_432.png")

    for f in sorted(out.glob("*.png")):
        print(f"[android] wrote {f.relative_to(root)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
