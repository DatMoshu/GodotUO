"""Offline pictures of multis: an isometric preview and a top-down plan per storey.

The preview draws static art the way the client places it (44x44 diamonds, 4 px
per z) in a simple back-to-front order. It is for eyeballing a layout before the
shard, not a parity reference: GUO in game is the proof.
"""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from guo.uoart import decode_static  # noqa: E402
from guo.uoread import Art, TileData  # noqa: E402
from multifile import Component  # noqa: E402
from pieces import BACKGROUND, role  # noqa: E402

_cache: dict = {}


def _sources(data_dir: Path):
    key = str(data_dir)
    if key not in _cache:
        _cache[key] = (Art(data_dir), TileData(data_dir), {})
    return _cache[key]


def sprite(data_dir: Path, item: int) -> Image.Image | None:
    art, _td, sprites = _sources(data_dir)
    if item not in sprites:
        raw = art.get_static(item)
        img = None
        if raw:
            w, h, px = decode_static(raw)
            img = Image.new("RGBA", (w, h))
            img.putdata([(0, 0, 0, 0) if v == 0 else
                         (((v >> 10) & 31) << 3, ((v >> 5) & 31) << 3, (v & 31) << 3, 255) for v in px])
        sprites[item] = img
    return sprites[item]


def render(comps: list[Component], data_dir: Path, png: Path | None = None, hidden: bool = False,
           max_z: int | None = None, bg=(24, 24, 28, 255)) -> Image.Image:
    _art, td, _ = _sources(data_dir)
    shown = [c for c in comps if (c.visible or hidden) and (max_z is None or c.z <= max_z)]
    if not shown:
        shown = comps[:1]

    def order(c: Component):
        t = td.static(c.item) or {"flags": 0, "height": 0}
        return (c.x + c.y, c.z + (0 if t["flags"] & BACKGROUND else 1), t["height"] > 0, c.item)

    shown.sort(key=order)
    placed = []
    for c in shown:
        img = sprite(data_dir, c.item)
        if img is None:
            continue
        sx = (c.x - c.y) * 22 - img.width // 2 + 22
        sy = (c.x + c.y) * 22 - c.z * 4 - img.height + 44
        placed.append((sx, sy, img))
    if not placed:
        out = Image.new("RGBA", (64, 64), bg)
    else:
        x0 = min(p[0] for p in placed) - 8
        y0 = min(p[1] for p in placed) - 8
        x1 = max(p[0] + p[2].width for p in placed) + 8
        y1 = max(p[1] + p[2].height for p in placed) + 8
        out = Image.new("RGBA", (x1 - x0, y1 - y0), bg)
        for sx, sy, img in placed:
            out.alpha_composite(img, (sx - x0, sy - y0))
    if png:
        png.parent.mkdir(parents=True, exist_ok=True)
        out.save(png)
    return out


ROLE_COLOURS = {"wall": (170, 170, 170), "post": (120, 120, 120), "window": (90, 160, 230),
                "door": (230, 160, 40), "floor": (110, 80, 50), "stair": (200, 60, 200),
                "roof": (160, 50, 40), "deco": (60, 160, 80), "marker": (255, 255, 0)}


def plan(comps: list[Component], data_dir: Path, z_from: int, z_to: int, cell: int = 12) -> Image.Image:
    """Top-down plan of the components with z_from <= z < z_to, coloured by role."""
    _art, td, _ = _sources(data_dir)
    sel = [c for c in comps if z_from <= c.z < z_to]
    if not sel:
        return Image.new("RGB", (cell, cell), (0, 0, 0))
    xs, ys = [c.x for c in comps], [c.y for c in comps]
    x0, y0 = min(xs), min(ys)
    img = Image.new("RGB", ((max(xs) - x0 + 1) * cell, (max(ys) - y0 + 1) * cell), (20, 20, 20))
    d = ImageDraw.Draw(img)
    rank = {"floor": 0, "deco": 1, "roof": 1, "stair": 2, "wall": 3, "post": 3, "window": 4, "door": 5, "marker": 6}
    for c in sorted(sel, key=lambda c: rank[role(td.static(c.item) or {"flags": 0, "height": 0, "name": ""}, c.visible)]):
        r = role(td.static(c.item) or {"flags": 0, "height": 0, "name": ""}, c.visible)
        px, py = (c.x - x0) * cell, (c.y - y0) * cell
        d.rectangle([px, py, px + cell - 2, py + cell - 2], fill=ROLE_COLOURS[r])
    return img


def sheet(images: list[tuple[str, Image.Image]], png: Path, cols: int = 6, tile: int = 300) -> None:
    """A contact sheet: each image scaled to fit a tile (nearest neighbour), captioned."""
    rows = (len(images) + cols - 1) // cols
    out = Image.new("RGB", (cols * tile, rows * (tile + 16)), (16, 16, 16))
    d = ImageDraw.Draw(out)
    for n, (label, img) in enumerate(images):
        s = min(1.0, (tile - 4) / max(img.width, img.height))
        im = img.resize((max(1, int(img.width * s)), max(1, int(img.height * s))), Image.NEAREST) if s < 1 else img
        cx, cy = (n % cols) * tile, (n // cols) * (tile + 16)
        out.paste(im.convert("RGB"), (cx + (tile - im.width) // 2, cy + (tile - im.height) // 2))
        d.text((cx + 4, cy + tile + 2), label, fill=(230, 230, 230))
    png.parent.mkdir(parents=True, exist_ok=True)
    out.save(png)
