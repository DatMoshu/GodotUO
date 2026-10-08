"""Shelf packing of decoded images into 2048 pages (data_formats section 36).

The order is fixed by (height descending, id ascending), so the same install gives the same layout. An image whose
pixels are byte-identical to an earlier one (same size, same pixel digest) shares that image's rectangle.
"""

from __future__ import annotations

import hashlib
import struct
from dataclasses import dataclass

PAGE = 2048


@dataclass
class Placed:
    page: int
    x: int
    y: int
    w: int
    h: int
    pixels_sha256: str


def pixels_digest(w: int, h: int, rgba: bytes) -> str:
    """sha256 over w and h (4 bytes each, little-endian) and the RGBA bytes, row-major."""
    return hashlib.sha256(struct.pack("<II", w, h) + rgba).hexdigest()


def pack(images: dict[int, tuple[int, int, bytes]]) -> tuple[dict[int, Placed], list[bytearray]]:
    """images: id -> (w, h, rgba). Returns each id's rectangle and the pages as RGBA buffers."""
    order = sorted(images, key=lambda i: (-images[i][1], i))
    pages: list[bytearray] = []
    placed: dict[int, Placed] = {}
    owner: dict[tuple[int, int, str], int] = {}
    page = -1
    shelf_y = shelf_h = cursor = 0

    def new_page() -> None:
        nonlocal page, shelf_y, shelf_h, cursor
        pages.append(bytearray(PAGE * PAGE * 4))
        page += 1
        shelf_y = shelf_h = cursor = 0

    for i in order:
        w, h, rgba = images[i]
        if not (1 <= w <= PAGE and 1 <= h <= PAGE):
            raise ValueError(f"id {i}: {w}x{h} does not fit a page")
        digest = pixels_digest(w, h, rgba)
        twin = owner.get((w, h, digest))
        if twin is not None:
            p = placed[twin]
            placed[i] = Placed(p.page, p.x, p.y, w, h, digest)
            continue
        if page < 0:
            new_page()
        if cursor + w > PAGE:                       # next shelf
            shelf_y += shelf_h
            shelf_h = cursor = 0
        if shelf_y + h > PAGE:                      # next page
            new_page()
        if shelf_h == 0:
            shelf_h = h
        stride = w * 4
        buf = pages[page]
        for row in range(h):
            at = ((shelf_y + row) * PAGE + cursor) * 4
            buf[at:at + stride] = rgba[row * stride:(row + 1) * stride]
        placed[i] = Placed(page, cursor, shelf_y, w, h, digest)
        owner[(w, h, digest)] = i
        cursor += w
    return {i: placed[i] for i in sorted(placed)}, pages


def crop(page: bytes | bytearray, x: int, y: int, w: int, h: int) -> bytes:
    stride = PAGE * 4
    return b"".join(bytes(page[(y + r) * stride + x * 4:(y + r) * stride + (x + w) * 4]) for r in range(h))
