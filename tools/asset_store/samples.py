"""Publish sample packs for the kinds the store has no real content for yet.

The ten shipped backgrounds (seed.py) are real. Themes, sounds and profile
presets have no community packs yet, so the catalogue would show three empty
shelves. These samples fill them so the store can be seen and tested end to
end. Every sample:

- has an id starting `sample-` and the author "GodotUO sample", which the web
  catalogue shows as a sample badge (docs/data_formats.md section 12);
- is generated here, from code, CC0: palettes, synthesised sound, drawn
  previews. Nothing is read from a UO install or from anywhere else;
- passes the same validation and publication as a real pack.

    python tools/asset_store/samples.py [--store-dir DIR]

Samples are never published by seed.py; run this on purpose. Needs Pillow.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import random
import struct
import sys
import tempfile
import wave
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config  # noqa: E402
from asset_store.pack import PACK_SCHEMA  # noqa: E402
from asset_store.run import publish  # noqa: E402

AUTHOR = "GodotUO sample"
SIZE = (960, 540)

THEMES = [
    ("sample-theme-lantern", "Lantern light", {
        "ground": "#2b2118", "panel": "#3d2f22", "frame": "#8a6a44",
        "text": "#f1e4cc", "accent": "#e6a94f"}),
    ("sample-theme-moonglow", "Moonglow slate", {
        "ground": "#1c2230", "panel": "#27304a", "frame": "#6c7ea8",
        "text": "#e3e8f4", "accent": "#9fb4ff"}),
    ("sample-theme-deepwood", "Deepwood moss", {
        "ground": "#18231c", "panel": "#223226", "frame": "#5e7a4f",
        "text": "#e2ead9", "accent": "#b6d37a"}),
]

SOUNDS = [
    ("sample-sound-rain", "Gentle rain", "rain"),
    ("sample-sound-forge", "Distant forge", "forge"),
    ("sample-sound-crickets", "Night crickets", "crickets"),
]

PRESETS = [
    ("sample-preset-one-thumb", "One-thumb handheld", "phone", [
        ("World", (0.0, 0.0, 1.0, 0.82)), ("Touch bar", (0.0, 0.84, 1.0, 1.0)),
        ("Backpack", (0.66, 0.08, 0.98, 0.52))]),
    ("sample-preset-dual-journal", "Dual screen, journal below", "dual", [
        ("World", (0.0, 0.0, 1.0, 0.56)), ("Journal", (0.02, 0.62, 0.62, 0.98)),
        ("Backpack", (0.66, 0.62, 0.98, 0.98))]),
    ("sample-preset-deck-large", "Steam Deck, large text", "deck", [
        ("World", (0.0, 0.0, 0.72, 1.0)), ("Status", (0.74, 0.02, 0.99, 0.30)),
        ("Journal", (0.74, 0.34, 0.99, 0.98))]),
]


def font(size: int) -> ImageFont.ImageFont:
    for name in ("seguisb.ttf", "segoeui.ttf", "DejaVuSans.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def png(image: Image.Image) -> bytes:
    out = io.BytesIO()
    image.save(out, "PNG", optimize=True)
    return out.getvalue()


def theme_preview(palette: dict) -> bytes:
    im = Image.new("RGB", SIZE, palette["ground"])
    d = ImageDraw.Draw(im)
    # A gump-like window drawn in the theme: frame, title strip, two rows, a button.
    x0, y0, x1, y1 = 180, 70, 780, 400
    d.rounded_rectangle((x0 - 8, y0 - 8, x1 + 8, y1 + 8), 10, fill=palette["frame"])
    d.rounded_rectangle((x0, y0, x1, y1), 6, fill=palette["panel"])
    d.rectangle((x0, y0, x1, y0 + 54), fill=palette["frame"])
    d.text((x0 + 24, y0 + 12), "Backpack", font=font(26), fill=palette["text"])
    for row in range(2):
        for col in range(5):
            cx, cy = x0 + 36 + col * 108, y0 + 84 + row * 112
            d.rounded_rectangle((cx, cy, cx + 84, cy + 84), 8, outline=palette["frame"], width=3)
    d.rounded_rectangle((x1 - 190, y1 - 16, x1 - 24, y1 + 34), 8, fill=palette["accent"])
    d.text((x1 - 160, y1 - 8), "Close", font=font(24), fill=palette["ground"])
    # The palette itself along the bottom, so the swatches read at thumbnail size.
    keys = list(palette)
    w = SIZE[0] // len(keys)
    d.rectangle((0, SIZE[1] - 70, SIZE[0], SIZE[1]), fill="#0e1422")
    for i, k in enumerate(keys):
        d.rectangle((i * w + 3, SIZE[1] - 64, (i + 1) * w - 3, SIZE[1]), fill=palette[k])
    return png(im)


def synth(kind: str, seconds: float = 4.0, rate: int = 22050) -> list[float]:
    rnd = random.Random(kind)
    n = int(seconds * rate)
    out = [0.0] * n
    if kind == "rain":
        lp = 0.0
        for i in range(n):
            lp += 0.08 * (rnd.uniform(-1, 1) - lp)
            drop = rnd.random() < 0.0015
            out[i] = 0.35 * lp + (rnd.uniform(-0.6, 0.6) if drop else 0.0)
    elif kind == "forge":
        for strike in range(5):
            start = int((0.35 + strike * 0.72) * rate)
            for j in range(int(0.6 * rate)):
                if start + j >= n:
                    break
                t = j / rate
                env = math.exp(-t * 9)
                out[start + j] += env * 0.6 * (math.sin(2 * math.pi * 523 * t)
                                               + 0.5 * math.sin(2 * math.pi * 1310 * t))
        lp = 0.0
        for i in range(n):
            lp += 0.01 * (rnd.uniform(-1, 1) - lp)
            out[i] += 0.5 * lp
    else:  # crickets
        for i in range(n):
            t = i / rate
            pulse = 1.0 if (t * 3.2) % 1.0 < 0.18 and (t * 48) % 1.0 < 0.55 else 0.0
            out[i] = 0.3 * pulse * math.sin(2 * math.pi * 4400 * t)
    peak = max(1e-6, max(abs(v) for v in out))
    return [v / peak * 0.8 for v in out]


def wav(samples: list[float], rate: int = 22050) -> bytes:
    out = io.BytesIO()
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(v * 32767)) for v in samples))
    return out.getvalue()


def sound_preview(samples: list[float]) -> bytes:
    im = Image.new("RGB", SIZE, "#1a2233")
    d = ImageDraw.Draw(im)
    mid, bars = SIZE[1] // 2, 120
    per = len(samples) // bars
    bw = SIZE[0] / bars
    for b in range(bars):
        chunk = samples[b * per:(b + 1) * per]
        amp = math.sqrt(sum(v * v for v in chunk) / max(1, len(chunk)))
        h = max(3, int(amp * 520))
        x = int(b * bw)
        d.rounded_rectangle((x + 2, mid - h, x + int(bw) - 2, mid + h), 2, fill="#9fb4ff")
    return png(im)


def preset_preview(device: str, regions: list) -> bytes:
    im = Image.new("RGB", SIZE, "#1a2233")
    d = ImageDraw.Draw(im)
    screens = [(120, 40, 840, 500)] if device != "dual" else [(200, 20, 760, 262), (200, 278, 760, 520)]
    if device == "deck":
        screens = [(80, 70, 880, 470)]
    for sx0, sy0, sx1, sy1 in screens:
        d.rounded_rectangle((sx0 - 10, sy0 - 10, sx1 + 10, sy1 + 10), 18, fill="#0e1422")
        d.rectangle((sx0, sy0, sx1, sy1), fill="#232d44")
    if device == "dual":
        (ax0, ay0, ax1, ay1), (bx0, by0, bx1, by1) = screens
        box = (ax0, ay0, bx1, by1)
    else:
        box = screens[0]
    bx0, by0, bx1, by1 = box
    for name, (u0, v0, u1, v1) in regions:
        r = (bx0 + u0 * (bx1 - bx0) + 4, by0 + v0 * (by1 - by0) + 4,
             bx0 + u1 * (bx1 - bx0) - 4, by0 + v1 * (by1 - by0) - 4)
        fill = "#33415f" if name == "World" else "#4a5a82"
        d.rounded_rectangle(r, 6, fill=fill, outline="#9fb4ff", width=2)
        d.text((r[0] + 14, r[1] + 10), name, font=font(24), fill="#e3e8f4")
    return png(im)


def build(pid: str, kind: str, title: str, payload: dict, folder: Path) -> Path:
    manifest = dict(schema=PACK_SCHEMA, id=pid, version="1.0.0", kind=kind, title=title,
                    author=AUTHOR, licence="CC0-1.0", min_profile_version=6, preview="preview.png",
                    files={n: hashlib.sha256(b).hexdigest() for n, b in payload.items()})
    path = folder / f"{pid}.zip"
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in {"manifest.json": (json.dumps(manifest, indent=2) + "\n").encode(), **payload}.items():
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return path


def samples(root: Path) -> None:
    note = b"Sample pack generated by tools/asset_store/samples.py. CC0-1.0.\n"
    with tempfile.TemporaryDirectory() as tmp:
        folder = Path(tmp)
        packs = []
        for pid, title, palette in THEMES:
            packs.append(build(pid, "theme", title, {
                "preview.png": theme_preview(palette),
                "theme.json": (json.dumps({"palette": palette}, indent=2) + "\n").encode(),
                "README.txt": note}, folder))
        for pid, title, kind in SOUNDS:
            s = synth(kind)
            packs.append(build(pid, "sound", title, {
                "preview.png": sound_preview(s), f"{kind}.wav": wav(s), "README.txt": note}, folder))
        for pid, title, device, regions in PRESETS:
            layout = {"device": device, "regions": [{"name": n, "rect": r} for n, r in regions]}
            packs.append(build(pid, "profile-preset", title, {
                "preview.png": preset_preview(device, regions),
                "preset.json": (json.dumps(layout, indent=2) + "\n").encode(),
                "README.txt": note}, folder))
        for path in packs:
            publish(path, root)
            print("Published sample", path.stem)


if __name__ == "__main__":
    config = load_config()
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--store-dir", type=Path, default=config.store_dir)
    samples(parser.parse_args().store_dir)
