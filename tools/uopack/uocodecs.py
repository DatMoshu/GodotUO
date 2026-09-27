"""Codecs for tools/uopack: raw client entries <-> pixels, exactly.

Static art, land and gumps reuse tools/guo/uoart.py (which mirrors the loaders
and the editor's encoder). This module adds what bulk in/out needs on top:

- static art keeps its 4-byte header as read (uoart writes 0; the client never
  reads it, but a byte-exact round trip must keep it);
- land keeps any bytes past the 1,012 diamond pixels (UOP entries are padded
  to 2,048 bytes);
- animation frames (MUL groups), which uoart does not cover.

A MUL animation group (AnimationsLoader.ReadMULAnimationFrames): a 256-entry
palette of 15-bit colours (512 bytes); uint32 frame count; uint32 offsets,
relative to the count field; then per frame int16 centre x, centre y, width,
height and runs until 0x7FFF7FFF. A run header is x << 22 | y << 12 | length
(10-bit signed x and y, 12-bit length), with x relative to the centre x and y
to centre y + height; then `length` palette indices. A pixel no run covers is
transparent: an animation frame has no transparent colour, which is why a
decoded frame is a list of palette indices with -1 for "not covered".
"""

from __future__ import annotations

import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import uoart  # noqa: E402

END = 0x7FFF7FFF
MAX_RUN = 0x0FFF


# --- static art and land: uoart plus what a byte-exact round trip keeps -------

def decode_static(raw: bytes) -> tuple[int, int, list[int], int]:
    header = struct.unpack_from("<I", raw, 0)[0]
    w, h, px = uoart.decode_static(raw)
    return w, h, px, header


def encode_static(px: list[int], w: int, h: int, header: int = 0) -> bytes:
    body = uoart.encode_static(px, w, h)
    return struct.pack("<I", header) + body[4:]


def decode_land(raw: bytes) -> tuple[list[int], bytes]:
    return uoart.decode_land(raw), raw[1012 * 2:]


def encode_land(px: list[int], tail: bytes = b"") -> bytes:
    return uoart.encode_land(px) + tail


def decode_gump(rows: bytes, w: int, h: int) -> list[int]:
    return uoart.decode_gump(rows, w, h)


def encode_gump(px: list[int], w: int, h: int) -> bytes:
    return uoart.encode_gump(px, w, h)


# --- animation groups ----------------------------------------------------------

@dataclass
class AnimFrame:
    center_x: int
    center_y: int
    width: int
    height: int
    index: list[int] = field(default_factory=list)   # palette index per pixel, -1 = not covered


@dataclass
class AnimGroup:
    palette: list[int]            # 256 15-bit colours
    frames: list[AnimFrame]
    offsets: list[int] | None = None   # as read; kept only to report layout differences


def _signed10(v: int) -> int:
    return v - 0x400 if v & 0x200 else v


def decode_anim(raw: bytes) -> AnimGroup:
    palette = list(struct.unpack_from("<256H", raw, 0))
    base = 512
    count = struct.unpack_from("<I", raw, base)[0]
    offsets = list(struct.unpack_from(f"<{count}I", raw, base + 4))
    frames = []
    for off in offsets:
        p = base + off
        cx, cy, w, h = struct.unpack_from("<hhhh", raw, p)
        p += 8
        frame = AnimFrame(cx, cy, w, h, [-1] * max(0, w * h))
        if w > 0 and h > 0:
            while p + 4 <= len(raw):
                header = struct.unpack_from("<I", raw, p)[0]
                p += 4
                if header == END:
                    break
                run = header & MAX_RUN
                x = _signed10((header >> 22) & 0x3FF) + cx
                y = _signed10((header >> 12) & 0x3FF) + cy + h
                start = y * w + x
                if not (0 <= y < h and 0 <= x and x + run <= w):
                    raise ValueError(f"run {x},{y}+{run} outside a {w}x{h} frame")
                frame.index[start:start + run] = raw[p:p + run]
                p += run
        frames.append(frame)
    return AnimGroup(palette, frames, offsets)


def _encode_frame(f: AnimFrame) -> bytes:
    out = bytearray(struct.pack("<hhhh", f.center_x, f.center_y, f.width, f.height))
    if f.width > 0 and f.height > 0:
        for y in range(f.height):
            row = f.index[y * f.width:(y + 1) * f.width]
            x = 0
            while x < f.width:
                if row[x] < 0:
                    x += 1
                    continue
                start = x
                while x < f.width and row[x] >= 0 and x - start < MAX_RUN:
                    x += 1
                ox, oy = start - f.center_x, y - f.center_y - f.height
                if not (-512 <= ox < 512 and -512 <= oy < 512):
                    raise ValueError(f"a run at {start},{y} is {ox},{oy} from the frame's centre; "
                                     "the format holds -512..511 (10-bit x and y)")
                rx, ry = ox & 0x3FF, oy & 0x3FF
                out += struct.pack("<I", (rx << 22) | (ry << 12) | (x - start))
                out += bytes(row[start:x])
    out += struct.pack("<I", END)
    return bytes(out)


def encode_anim(group: AnimGroup) -> bytes:
    bodies = [_encode_frame(f) for f in group.frames]
    count = len(bodies)
    offsets = []
    at = 4 + 4 * count
    for b in bodies:
        offsets.append(at)
        at += len(b)
    return (struct.pack("<256H", *group.palette) + struct.pack("<I", count)
            + struct.pack(f"<{count}I", *offsets) + b"".join(bodies))


def color_rgba(c16: int) -> tuple[int, int, int, int]:
    """15-bit colour to 8-bit RGBA the way HuesHelper.Color16To32 expands it."""
    r, g, b = (c16 >> 10) & 0x1F, (c16 >> 5) & 0x1F, c16 & 0x1F
    return (r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2), 255
