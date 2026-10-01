#!/usr/bin/env python3
r"""Build every app icon GUO ships from the brand sigil, so no build on any
platform shows the engine's logo.

    python tools\brand\run.py                 build them all
    python tools\brand\run.py --sheet <png>   also write a contact sheet
    launchers\dev\brand_icons.bat             the same, from a launcher

The master is design\brand\guo-sigil.png: the ornate gold-and-silver sigil,
1254 x 1254 RGBA on a transparent background. It is committed here so the
build has no dependency outside the repository. From it this tool writes:

    godot\GUO\icon.png              256 px, the sigil on a dark rounded slab.
                                    config/icon: the window and taskbar icon
                                    at runtime.
    godot\GUO\icon.ico              16, 24, 32, 48, 64, 128, 256 px, each
                                    rendered from the master, not downscaled
                                    from the 256. config/windows_native_icon
                                    and the Windows preset's application/icon:
                                    the executable's icon.
    godot\GUO\splash.png            1280 x 720, the sigil centred on the same
                                    dark: application/boot_splash/image.
    godot\GUO\assets\brand\splash_sigil.png
                                    the master itself, for the intro that
                                    plays on start (src/Bootstrap/SplashIntro.cs),
                                    which scales it at runtime, so it stays
                                    sharp at any resolution.
    godot\GUO\android_icons\
        main_192.png                legacy launcher icon: the slab again
        foreground_432.png          adaptive foreground: the sigil inside the
                                    inner 66% safe zone, on transparent
        background_432.png          adaptive background: flat dark
        monochrome_432.png          adaptive monochrome: the sigil's alpha, white

The slab is near-black (#14100C, a warm dark) rather than pure black so the
sigil's darkest engraving still reads against it, and so the icon sits on a
light taskbar as well as a dark one. Only the adaptive foreground keeps the
transparency: Android composes it over the background layer itself.

This is the brand, not UO pixel art: Lanczos resampling throughout
(AGENTS.md rule 7 is about the game's art, and does not apply here).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import load_config  # noqa: E402

MASTER = Path("design") / "brand" / "guo-sigil.png"

BACKGROUND = (0x14, 0x10, 0x0C, 255)
WHITE = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
ICON = 256
LEGACY = 192
ADAPTIVE = 432
SPLASH = (1280, 720)

SLAB_RADIUS = 0.20   # corner radius as a fraction of the slab's side
SLAB_FILL = 0.84     # how much of the slab the sigil spans
SAFE_ZONE = 0.66     # Android masks an adaptive icon to its inner 66%
SPLASH_HEIGHT = 0.62 # the sigil's height as a fraction of the splash height


def fitted(master: Image.Image, size: int, fraction: float) -> Image.Image:
    """The sigil's opaque extent scaled to `fraction` of `size`, centred on transparent."""
    bbox = master.getbbox()
    mark = master.crop(bbox) if bbox else master
    box = size * fraction
    scale = min(box / mark.width, box / mark.height)
    w, h = max(1, round(mark.width * scale)), max(1, round(mark.height * scale))
    out = Image.new("RGBA", (size, size), CLEAR)
    out.alpha_composite(mark.resize((w, h), Image.LANCZOS), ((size - w) // 2, (size - h) // 2))
    return out


def slab(size: int) -> Image.Image:
    """A dark rounded square, drawn 4x oversize and brought down so its edge is smooth."""
    over = 4
    big = size * over
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=int(big * SLAB_RADIUS), fill=255)
    mask = mask.resize((size, size), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), BACKGROUND)
    out.putalpha(mask)
    return out


def on_slab(master: Image.Image, size: int) -> Image.Image:
    out = slab(size)
    out.alpha_composite(fitted(master, size, SLAB_FILL))
    return out


def monochrome(foreground: Image.Image) -> Image.Image:
    """The sigil's alpha as white: Android tints it to the theme."""
    out = Image.new("RGBA", foreground.size, WHITE)
    out.putalpha(foreground.getchannel("A"))
    return out


def splash(master: Image.Image) -> Image.Image:
    w, h = SPLASH
    out = Image.new("RGBA", (w, h), BACKGROUND)
    mark = fitted(master, int(h * SPLASH_HEIGHT), 1.0)
    out.alpha_composite(mark, ((w - mark.width) // 2, (h - mark.height) // 2))
    return out


def write_ico(master: Image.Image, path: Path) -> None:
    frames = [on_slab(master, s) for s in ICO_SIZES]
    largest = frames[-1]
    # Pillow writes the first image and then `append_images`; each frame is
    # rendered at its own size above, so the 16 px one is not a blurred 256.
    largest.save(path, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])


def build(root: Path) -> list[Path]:
    master_path = root / MASTER
    if not master_path.is_file():
        sys.exit(f"[brand] no master at {master_path}")
    master = Image.open(master_path).convert("RGBA")
    # The sigil once arrived with its "transparency" painted in as a
    # checkerboard (alpha 255 everywhere); tools\brand\unkey.py fixed that.
    # An opaque corner means it has happened again: refuse, do not ship it.
    if master.getpixel((0, 0))[3] != 0 or master.getpixel((master.width - 1, master.height - 1))[3] != 0:
        sys.exit(f"[brand] {master_path} is opaque in its corners: the background is painted, not "
                 "transparent. Run tools\\brand\\unkey.py on it first.")
    if master.size != (1254, 1254):
        print(f"[brand] note: master is {master.size[0]}x{master.size[1]}, not the 1254 the sigil was delivered at")
    print(f"[brand] master: {master_path.relative_to(root)} {master.size[0]}x{master.size[1]} {master.mode}")

    project = root / "godot" / "GUO"
    android = project / "android_icons"
    android.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []

    def save(img: Image.Image, path: Path) -> None:
        img.save(path)
        written.append(path)

    save(on_slab(master, ICON), project / "icon.png")
    write_ico(master, project / "icon.ico")
    written.append(project / "icon.ico")
    save(splash(master), project / "splash.png")
    (project / "assets" / "brand").mkdir(parents=True, exist_ok=True)
    save(master, project / "assets" / "brand" / "splash_sigil.png")

    save(on_slab(master, LEGACY), android / "main_192.png")
    fg = fitted(master, ADAPTIVE, SAFE_ZONE)
    save(fg, android / "foreground_432.png")
    save(Image.new("RGBA", (ADAPTIVE, ADAPTIVE), BACKGROUND), android / "background_432.png")
    save(monochrome(fg), android / "monochrome_432.png")

    for f in written:
        print(f"[brand] wrote {f.relative_to(root)}")
    return written


def sheet(root: Path, path: Path) -> None:
    """Every output side by side, each on a light and a dark strip, for a human to look at."""
    project = root / "godot" / "GUO"
    items = [
        ("icon.png 256", Image.open(project / "icon.png").convert("RGBA")),
        ("icon.ico 32", _ico_frame(project / "icon.ico", 32)),
        ("icon.ico 16", _ico_frame(project / "icon.ico", 16)),
        ("android main 192", Image.open(project / "android_icons" / "main_192.png").convert("RGBA")),
        ("adaptive fg 432", Image.open(project / "android_icons" / "foreground_432.png").convert("RGBA")),
        ("adaptive bg 432", Image.open(project / "android_icons" / "background_432.png").convert("RGBA")),
        ("monochrome 432", Image.open(project / "android_icons" / "monochrome_432.png").convert("RGBA")),
    ]
    cell, pad = 160, 12
    cols = len(items)
    width = cols * (cell + pad) + pad
    strip = cell + pad * 2
    out = Image.new("RGBA", (width, strip * 2 + 720 // 3 + pad * 2), (0x1E, 0x1E, 0x1E, 255))
    ImageDraw.Draw(out).rectangle((0, 0, width, strip), fill=(0xF0, 0xF0, 0xF0, 255))
    ImageDraw.Draw(out).rectangle((0, strip, width, strip * 2), fill=(0x20, 0x20, 0x20, 255))
    for i, (_, img) in enumerate(items):
        x = pad + i * (cell + pad)
        shown = img if img.width <= cell else img.resize((cell, cell), Image.LANCZOS)
        ox = x + (cell - shown.width) // 2
        for row in range(2):
            y = row * strip + pad + (cell - shown.height) // 2
            out.alpha_composite(shown, (ox, y))
    sp = Image.open(project / "splash.png").convert("RGBA").resize((1280 // 3, 720 // 3), Image.LANCZOS)
    out.alpha_composite(sp, ((width - sp.width) // 2, strip * 2 + pad))
    out.save(path)
    print(f"[brand] sheet: {path}")


def _ico_frame(path: Path, size: int) -> Image.Image:
    ico = Image.open(path)
    ico.size = (size, size)
    ico.load()
    return ico.convert("RGBA")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sheet", default=None, help="also write a contact sheet PNG of everything built")
    args = parser.parse_args(argv)
    root = load_config().root
    build(root)
    if args.sheet:
        sheet(root, Path(args.sheet))
    return 0


if __name__ == "__main__":
    sys.exit(main())
