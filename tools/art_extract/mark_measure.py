r"""Measure how well the AX7 mark survives, on synthetic art only (rule 8: no game data).

    python tools\art_extract\mark_measure.py [--trials 120] [--still PATH]

Prints, for a sprite-like page (about half opaque, some dark outline pixels the mark skips):
  * the smallest crop side N at which every trial decodes, and the success rate for smaller sides
  * PNG re-save (this tool's own encoder, and Pillow with optimisation if Pillow is installed)
  * 5-bit requantisation (drop the low bits and expand the way the loaders do)
  * known non-survivors: JPEG, scaling, a hued or lit copy
and with --still writes a side-by-side still: original | marked | difference x32.
"""

from __future__ import annotations

import argparse
import io
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import art_mark as m  # noqa: E402
import art_png as png  # noqa: E402

EXPAND = np.array((0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39, 0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B, 0x83, 0x8B,
                   0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD, 0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF), dtype=np.uint8)
SECRET = bytes(range(7, 39))                       # a made-up key; belongs to nothing
SHARD, SERIAL = "demo-shard", 1234


def sprite_page(side: int = 512, seed: int = 5, density: float = 0.55) -> np.ndarray:
    """Blobs of smoothly shaded 1555-expanded colour on a transparent ground, with a dark outline."""
    rng = np.random.default_rng(seed)
    page = np.zeros((side, side, 4), dtype=np.uint8)
    yy, xx = np.mgrid[0:side, 0:side]
    covered = 0
    while covered < density * side * side:
        cx, cy = rng.integers(0, side, 2)
        rx, ry = rng.integers(10, 40, 2)
        inside = ((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2 <= 1.0
        edge = inside & (((xx - cx) / (rx - 2)) ** 2 + ((yy - cy) / (ry - 2)) ** 2 > 1.0)
        base = rng.integers(5, 26, 3)
        shade = ((xx - cx) * rng.uniform(-0.2, 0.2) + (yy - cy) * rng.uniform(-0.2, 0.2)).astype(int)
        k = np.clip(base[None, None, :] + shade[..., None] // 2 + rng.integers(-1, 2, (side, side, 3)), 0, 31)
        px = EXPAND[k]
        page[inside, :3] = px[inside]
        page[inside, 3] = 255
        page[edge, :3] = EXPAND[rng.integers(0, 2, (int(edge.sum()), 3))]
        covered = int(np.count_nonzero(page[..., 3]))
    return page


def try_read(rgba) -> bool:
    found = m.read_rgba(np.ascontiguousarray(rgba))
    return bool(found and found["shard_id"] == SHARD and found["serial"] == SERIAL)


def crop_rates(marked: np.ndarray, sides, trials: int, rng, record=None) -> dict[int, float]:
    out = {}
    h, w = marked.shape[:2]
    for n in sides:
        ok = 0
        for _ in range(trials):
            y, x = int(rng.integers(0, h - n)), int(rng.integers(0, w - n))
            crop = marked[y:y + n, x:x + n]
            got = try_read(crop)
            ok += got
            if record is not None:
                record.append((int(np.count_nonzero(m.eligible(crop))), got))
        out[n] = ok / trials
    return out


def carriers_needed(record) -> int:
    """The smallest carrier-pixel count c such that every crop with at least c carriers was read."""
    failed = [c for c, got in record if not got]
    return max(failed) + 1 if failed else 0


def requantise(rgba: np.ndarray) -> np.ndarray:
    out = rgba.copy()
    out[..., :3] = EXPAND[rgba[..., :3] >> 3]
    return out


def side_by_side(original: np.ndarray, marked: np.ndarray, path: Path, crop: int = 160) -> None:
    a, b = original[:crop, :crop], marked[:crop, :crop]
    diff = np.abs(a.astype(int) - b.astype(int))[..., :3]
    d = np.clip(diff * 32, 0, 255).astype(np.uint8)
    dd = np.concatenate([d, np.full((crop, crop, 1), 255, np.uint8)], axis=-1)
    ground = np.full((crop, crop, 4), (96, 96, 96, 255), np.uint8)

    def over(img):
        alpha = img[..., 3:4].astype(float) / 255
        return np.concatenate([(img[..., :3] * alpha + ground[..., :3] * (1 - alpha)).astype(np.uint8),
                               np.full((crop, crop, 1), 255, np.uint8)], axis=-1)

    gap = np.full((crop, 8, 4), 255, np.uint8)
    strip = np.concatenate([over(a), gap, over(b), gap, dd], axis=1)
    scale = 3
    strip = np.repeat(np.repeat(strip, scale, axis=0), scale, axis=1)          # nearest neighbour, as the client draws
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png.encode_rgba(strip.shape[1], strip.shape[0], strip.tobytes()))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trials", type=int, default=60)
    ap.add_argument("--still", help="write the side-by-side still here (under build/)")
    args = ap.parse_args(argv)
    rng = np.random.default_rng(99)
    payload = m.build_payload(SHARD, SERIAL, SECRET)
    page = sprite_page()
    marked = m.mark_rgba(page, payload)
    opaque = int(np.count_nonzero(page[..., 3] == 255))
    carriers = int(np.count_nonzero(m.eligible(page)))
    changed = int((marked != page).any(axis=-1).sum())
    print(f"page {page.shape[1]}x{page.shape[0]}: {opaque} opaque, {carriers} carry the mark ({100 * carriers / opaque:.0f}%), "
          f"{changed} pixels differ, max channel difference {int(np.abs(marked.astype(int) - page).max())}/255")
    print(f"whole page decodes: {try_read(marked)}; unmarked page decodes: {try_read(page)}")

    sides = (12, 16, 20, 24, 28, 32, 40, 48, 64, 96)
    record = []
    rates = crop_rates(marked, sides, args.trials, rng, record)
    print(f"crop of N x N px of marked sprite art ({args.trials} random crops each):")
    for n, r in rates.items():
        print(f"  N={n:3}  {100 * r:5.1f}% read")
    full = [n for n, r in rates.items() if r == 1.0]
    print(f"smallest side where every crop was read: N = {min(full) if full else 'none of the sides tried'}")

    print(f"every crop that held at least {carriers_needed(record)} carrier pixels was read "
          f"({sum(1 for c, g in record if c >= carriers_needed(record))} such crops)")
    solid = np.zeros_like(page)
    solid[..., :3] = EXPAND[np.random.default_rng(3).integers(2, 31, (page.shape[0], page.shape[1], 3))]
    solid[..., 3] = 255
    solid_marked = m.mark_rgba(solid, payload)
    srates = crop_rates(solid_marked, (8, 12, 16, 24, 32), args.trials, rng)
    print("same on a fully opaque page: " + ", ".join(f"N={n} {100 * r:.0f}%" for n, r in srates.items()))

    print("what the mark survives and what it does not:")
    print(f"  PNG re-save, this tool's encoder:              {try_read(np.frombuffer(png.decode_any(png.encode_rgba(page.shape[1], page.shape[0], marked.tobytes()))[2], np.uint8).reshape(page.shape))}")
    try:
        from PIL import Image
        buf = io.BytesIO()
        Image.fromarray(marked, "RGBA").save(buf, "PNG", optimize=True)
        w, h, raw = png.decode_any(buf.getvalue())
        print(f"  PNG re-save, Pillow optimised (adaptive filters): {try_read(np.frombuffer(raw, np.uint8).reshape(h, w, 4))}")
        buf = io.BytesIO()
        Image.fromarray(marked[..., :3], "RGB").save(buf, "JPEG", quality=95)
        jpg = np.array(Image.open(buf).convert("RGBA"))
        print(f"  JPEG quality 95:                               {try_read(jpg)}")
        big = np.array(Image.fromarray(marked, "RGBA").resize((page.shape[1] * 2, page.shape[0] * 2), Image.NEAREST))
        print(f"  scaled x2 (nearest):                           {try_read(big)}")
        small = np.array(Image.fromarray(marked, "RGBA").resize((page.shape[1] * 3 // 4, page.shape[0] * 3 // 4), Image.BILINEAR))
        print(f"  scaled x0.75 (bilinear):                       {try_read(small)}")
    except ImportError:
        print("  (Pillow is not installed: Pillow re-save, JPEG and scaling not measured)")
    print(f"  5-bit requantisation (low bits dropped):       {try_read(requantise(marked))}")
    lit = marked.copy()
    lit[..., :3] = (lit[..., :3].astype(int) * 0.85).astype(np.uint8)
    print(f"  darkened copy (x0.85, e.g. a lit screenshot):  {try_read(lit)}")
    if args.still:
        side_by_side(page, marked, Path(args.still))
        print(f"still written to {args.still}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
