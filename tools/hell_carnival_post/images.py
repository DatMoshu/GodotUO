"""Post images for the Hellmaw / Cosmic Carnival process post.

Reads Codex's review folder (read only) and writes labelled images into
build/hell_carnival_post/attachments: one wide A / B / B+props image per
dungeon at native scale, 2x nearest-neighbour A/B crops, and a seam sheet.

    python tools/hell_carnival_post/images.py [--src <review folder>]
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

from paths import ROOT, font_file, src_dir

SRC = src_dir()
OUT = ROOT / "build" / "hell_carnival_post" / "attachments"
BG, INK, SUB, GOLD = (19, 25, 30), (235, 238, 236), (170, 185, 188), (230, 200, 120)


def font(size, bold=False):
    return ImageFont.truetype(font_file("segoeuib.ttf" if bold else "segoeui.ttf"), size)


def shot(name):
    return Image.open(SRC / "guo" / name / "world_shot.png").convert("RGB")


def header(w, title, sub, h=78):
    im = Image.new("RGB", (w, h), (32, 43, 50))
    d = ImageDraw.Draw(im)
    d.text((20, 12), title, font=font(26, True), fill=INK)
    d.text((20, 46), sub, font=font(18), fill=SUB)
    return im


def wide(theme, title):
    names = [f"{theme}-chunks-original", f"{theme}-chunks-new", f"{theme}-chunks-decorated"]
    labels = ["A · original UO art", "B · new art (same X/Y/Z, IDs, hues)", "B + optional props (not a strict A/B)"]
    pw, ph, gap = 1100, 850, 12
    W = 3 * pw + 2 * gap
    hd = header(W, title, "GUO editor World renderer captures · native 1× pixels · same 3×3 copied map blocks, camera and season · editor render, not gameplay")
    im = Image.new("RGB", (W, hd.height + 44 + ph), BG)
    im.paste(hd, (0, 0))
    d = ImageDraw.Draw(im)
    for i, (n, l) in enumerate(zip(names, labels)):
        x = i * (pw + gap)
        d.text((x + 16, hd.height + 8), l, font=font(22, True), fill=GOLD if i == 2 else INK)
        im.paste(shot(n), (x, hd.height + 44))
    return im


def crops(theme, title, boxes):
    a, b = shot(f"{theme}-chunks-original"), shot(f"{theme}-chunks-new")
    rows = []
    for box, cap in boxes:
        ca, cb = a.crop(box), b.crop(box)
        ca = ca.resize((ca.width * 2, ca.height * 2), Image.NEAREST)
        cb = cb.resize((cb.width * 2, cb.height * 2), Image.NEAREST)
        gap = 12
        r = Image.new("RGB", (ca.width * 2 + gap, ca.height + 40), BG)
        d = ImageDraw.Draw(r)
        d.text((10, 6), "A · original  —  " + cap, font=font(20, True), fill=INK)
        d.text((ca.width + gap + 10, 6), "B · new art, same placements", font=font(20, True), fill=INK)
        r.paste(ca, (0, 40))
        r.paste(cb, (ca.width + gap, 40))
        rows.append(r)
    W = max(r.width for r in rows)
    hd = header(W, title, "2× nearest-neighbour crops of the same GUO editor captures · crop box in native pixels given per row")
    im = Image.new("RGB", (W, hd.height + sum(r.height + 16 for r in rows)), BG)
    im.paste(hd, (0, 0))
    y = hd.height + 8
    for r in rows:
        im.paste(r, (0, y))
        y += r.height + 16
    return im


def seams():
    names = ["hell_basalt", "hell_gore", "carnival_ivory", "carnival_deck"]
    s, gap = 512, 12
    hd = header(2 * s + gap, "Texmap wrap test", "Each 128×128 texmap tiled 4×4 · no visible seams; the 128 px repeat is visible on the busier materials")
    im = Image.new("RGB", (2 * s + gap, hd.height + 2 * (s + 36)), BG)
    im.paste(hd, (0, 0))
    d = ImageDraw.Draw(im)
    for i, n in enumerate(names):
        x, y = (i % 2) * (s + gap), hd.height + (i // 2) * (s + 36)
        d.text((x + 8, y + 4), n, font=font(20, True), fill=INK)
        im.paste(Image.open(SRC / "previews" / f"{n}-seams.png").convert("RGB"), (x, y + 32))
    return im


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    jobs = {
        "01_hellmaw_wide.png": wide("hell", "Hellmaw — The Furnace Below"),
        "02_hellmaw_crops.png": crops("hell", "Hellmaw · A/B native-scale crops", [
            ((0, 250, 450, 520), "rock ridge, box 0,250–450,520"),
            ((600, 470, 900, 760), "basin edge, box 600,470–900,760")]),
        "04_carnival_wide.png": wide("carnival", "Cosmic Carnival — Last Show on the Saucer"),
        "05_carnival_crops.png": crops("carnival", "Cosmic Carnival · A/B native-scale crops", [
            ((240, 60, 700, 360), "stairs and walls, box 240,60–700,360"),
            ((440, 440, 760, 720), "piers and walkway, box 440,440–760,720")]),
        "07_texmap_seams.png": seams(),
    }
    for name, im in jobs.items():
        im.save(OUT / name, optimize=True)
    for src, dst in [("hell-contact.png", "03_hellmaw_assets.png"), ("carnival-contact.png", "06_carnival_assets.png")]:
        (OUT / dst).write_bytes((SRC / "previews" / src).read_bytes())
    for p in sorted(OUT.glob("*.png")):
        w, h = Image.open(p).size
        print(f"{p.name:28s} {w}x{h} {p.stat().st_size / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
