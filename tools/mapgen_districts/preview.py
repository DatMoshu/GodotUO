"""Radar pictures of the composed map: the world project's land and statics in the client's radar
colours, with each district's multis (which the shard places, so no block holds them) drawn on top."""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from compose import read_block


def radar_colours(cfg) -> np.ndarray:
    raw = np.fromfile(Path(cfg.client_data) / "radarcol.mul", dtype="<u2")
    r = ((raw >> 10) & 31) * 255 // 31
    g = ((raw >> 5) & 31) * 255 // 31
    b = (raw & 31) * 255 // 31
    return np.stack([r, g, b], axis=1).astype(np.uint8)


def render(cfg, built: Path, width: int, height: int, origin) -> Image.Image:
    colours = radar_colours(cfg)
    ox, oy = origin
    img = np.zeros((height, width, 3), np.uint8)
    top = np.full((height, width), -128, np.int16)
    for path in (built / "world" / "blocks" / "0").glob("*.json"):
        block = read_block(path)
        bx, by = block["block"]
        x0, y0 = bx * 8 - ox, by * 8 - oy
        if not (0 <= x0 < width and 0 <= y0 < height):
            continue
        for row, line in enumerate(block["land"]):
            for col, cell in enumerate(line.split()):
                tile, z = cell.split(":")
                img[y0 + row, x0 + col] = colours[int(tile, 16)]
                top[y0 + row, x0 + col] = int(z)
        for s in block["statics"]:
            x, y = x0 + s["x"], y0 + s["y"]
            if s["z"] >= top[y, x]:
                img[y, x] = colours[0x4000 + int(s["id"], 16)]
                top[y, x] = s["z"]
    scene = json.loads((built / "scene.json").read_text(encoding="utf-8"))
    for part in scene["parts"]:
        cx, cy = part["centre"]
        comps = json.loads((built / "parts" / f"{part['name']}.json").read_text(encoding="utf-8"))
        for comp in sorted(comps, key=lambda c: c[3]):
            if len(comp) >= 5 and not comp[4]:
                continue
            x, y = comp[1] + cx, comp[2] + cy
            if 0 <= x < width and 0 <= y < height:
                img[y, x] = colours[0x4000 + comp[0]]
    return Image.fromarray(img)


def towns(cfg, out: Path, record: dict) -> list[str]:
    built = out / "built"
    w, h = record["size"]
    full = render(cfg, built, w, h, record["origin"])
    marked = full.copy()
    draw = ImageDraw.Draw(marked)
    for t in record["towns"]:
        x1, y1, x2, y2 = t["lot"]
        draw.rectangle([x1 - 2, y1 - 2, x2 + 2, y2 + 2], outline=(255, 220, 0), width=2)
        draw.text((x1, y1 - 14), f"town {t['town']}: {t['source']}", fill=(255, 255, 255))
    images = []
    full.save(out / "radar_towns_plain.png")
    marked.save(out / "radar_towns.png")
    images += [str(out / "radar_towns.png"), str(out / "radar_towns_plain.png")]
    crops = []
    for t in record["towns"]:
        x1, y1, x2, y2 = t["lot"]
        pad = 48
        crop = full.crop((max(0, x1 - pad), max(0, y1 - pad), min(w, x2 + pad), min(h, y2 + pad)))
        crop = crop.resize((crop.width * 4, crop.height * 4), Image.NEAREST)
        path = out / f"town{t['town']}_radar.png"
        crop.save(path)
        images.append(str(path))
        crops.append(crop)
    if crops:
        sheet = Image.new("RGB", (sum(c.width for c in crops) + 16 * (len(crops) - 1), max(c.height for c in crops)), (16, 16, 16))
        x = 0
        for c in crops:
            sheet.paste(c, (x, 0))
            x += c.width + 16
        sheet.save(out / "towns_radar_sheet.png")
        images.append(str(out / "towns_radar_sheet.png"))
    return images
