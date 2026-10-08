"""Write and read the one PNG shape an art set uses: 8-bit RGBA, no interlace, no ancillary chunks.

Deterministic: the same pixels give the same bytes (filter 0 on every row, zlib level 6, IHDR/IDAT/IEND only).
The reader accepts only that shape, so a page with extra chunks, another filter or a colour profile is refused.
"""

from __future__ import annotations

import struct
import zlib

SIGNATURE = b"\x89PNG\r\n\x1a\n"
LEVEL = 6


def _chunk(kind: bytes, body: bytes) -> bytes:
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)


def encode_rgba(width: int, height: int, rgba: bytes | bytearray) -> bytes:
    if len(rgba) != width * height * 4:
        raise ValueError("pixel buffer does not match the size")
    stride = width * 4
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]
    return (SIGNATURE
            + _chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + _chunk(b"IDAT", zlib.compress(bytes(raw), LEVEL))
            + _chunk(b"IEND", b""))


def decode_rgba(data: bytes) -> tuple[int, int, bytes]:
    if data[:8] != SIGNATURE:
        raise ValueError("not a PNG")
    at = 8
    header = None
    idat = bytearray()
    seen_end = False
    while at < len(data):
        if at + 12 > len(data):
            raise ValueError("PNG is cut short")
        (length,) = struct.unpack_from(">I", data, at)
        kind = data[at + 4:at + 8]
        body = data[at + 8:at + 8 + length]
        if at + 12 + length > len(data):
            raise ValueError("PNG is cut short")
        (crc,) = struct.unpack_from(">I", data, at + 8 + length)
        if zlib.crc32(kind + body) & 0xFFFFFFFF != crc:
            raise ValueError(f"{kind.decode('latin-1')} chunk fails its CRC")
        at += 12 + length
        if kind == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            seen_end = True
        else:
            raise ValueError(f"ancillary chunk {kind.decode('latin-1')!r} is not allowed in a page")
    if header is None or not seen_end or at != len(data):
        raise ValueError("PNG is cut short or has trailing bytes")
    width, height, depth, colour, comp, filt, interlace = header
    if (depth, colour, comp, filt, interlace) != (8, 6, 0, 0, 0):
        raise ValueError("PNG is not 8-bit RGBA without interlace")
    raw = zlib.decompress(bytes(idat))
    stride = width * 4
    if len(raw) != height * (stride + 1):
        raise ValueError("PNG data has the wrong length")
    out = bytearray()
    for y in range(height):
        base = y * (stride + 1)
        if raw[base] != 0:
            raise ValueError("PNG uses a row filter; pages are written with filter 0")
        out += raw[base + 1:base + 1 + stride]
    return width, height, bytes(out)
