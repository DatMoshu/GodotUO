"""Encode and decode UO art, gumps and hues, and write the verdata patch format.

Shared by tools/world (export and verify of a world project's assets/, ADR-0020).
The layouts follow godot/GUO/src/Assets/ArtLoader.cs, GumpsLoader.cs,
HuesLoader.cs and UOFileManager.cs (the verdata patch loop), and the editor's
encoder in godot/GUO/addons/guo_editor/Overlay/AssetOverlay.cs; keep the three
in step.

Colour is UO's 15-bit RGB (5 bits a channel, red highest). In statics and gumps
0 is transparent, so opaque black is stored as NEAR_BLACK. Land has no
transparency: 0 is black, and only the 1,012 pixels of the 44x44 diamond exist.

verdata.mul: int32 count, then count records of five uint32 (file id, block id,
position, length, extra), then the data. File id 4 is art (block = art index:
land id, or 0x4000 + static id), 12 is gumps (extra = width << 16 | height).
Upstream applies a non-empty verdata.mul on every client version.

texmaps.mul / texidx.mul: the textures sloped land is drawn with. Entry n is
the texture of every land tile whose tiledata TexID is n: 64x64 (0x2000 bytes)
or 128x128 (0x8000 bytes) of 16-bit colours, row by row, no transparency.
Upstream never applies verdata to them (UOFileManager skips file id 10), so a
replaced texmap ships as patched copies of the two files instead.

hues.mul: groups of a uint32 header and eight 88-byte hues (32 colours, table
start, table end, a 20-byte name). Hue n (1-based) is group (n-1)//8, entry
(n-1)%8.
"""

from __future__ import annotations

import json
import struct
from dataclasses import dataclass
from pathlib import Path

LAND_COUNT = 0x4000
NEAR_BLACK = 0x0421
VERDATA_ART = 4
VERDATA_GUMP = 12
VERDATA_RECORD = 20
HUE_BLOCK = 88
HUE_GROUP = 4 + 8 * HUE_BLOCK


def hue_offset(hue: int) -> int:
    """Where hue n (1-based) starts in hues.mul."""
    n = hue - 1
    return (n // 8) * HUE_GROUP + 4 + (n % 8) * HUE_BLOCK


def in_diamond(x: int, y: int) -> bool:
    if y < 22:
        start = 21 - y
        return start <= x < start + 2 * (y + 1)
    i = y - 22
    return i <= x < i + 2 * (22 - i)


def to16(r: int, g: int, b: int, a: int, land: bool) -> int:
    if not land and a < 128:
        return 0
    v = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)
    return NEAR_BLACK if v == 0 and not land else v


def png_pixels(path: Path, land: bool) -> tuple[int, int, list[int]]:
    """A PNG as UO colour, the way the editor reads it."""
    from PIL import Image  # Pillow; only needed for assets

    with Image.open(path) as im:
        im = im.convert("RGBA")
        w, h = im.size
        data = list(im.getdata())
    px = []
    for i, (r, g, b, a) in enumerate(data):
        x, y = i % w, i // w
        px.append(0 if land and not in_diamond(x, y) else to16(r, g, b, a, land))
    return w, h, px


# --- encoders (mirror AssetOverlay.cs) --------------------------------------

def encode_land(px: list[int]) -> bytes:
    return b"".join(struct.pack("<H", px[y * 44 + x]) for y in range(44) for x in range(44) if in_diamond(x, y))


def encode_static(px: list[int], w: int, h: int) -> bytes:
    data: list[int] = []
    rows = []
    for y in range(h):
        if len(data) > 0xFFFF:
            raise ValueError("static too detailed: its row table overflows")
        rows.append(len(data))
        x = at = 0
        while x < w:
            if px[y * w + x] == 0:
                x += 1
                continue
            start = x
            while x < w and px[y * w + x] != 0:
                x += 1
            data += [start - at, x - start] + px[y * w + start:y * w + x]
            at = x
        data += [0, 0]
    return struct.pack("<IHH", 0, w, h) + struct.pack(f"<{h}H", *rows) + struct.pack(f"<{len(data)}H", *data)


def encode_gump(px: list[int], w: int, h: int) -> bytes:
    rows: list[list[tuple[int, int]]] = []
    for y in range(h):
        row = []
        x = 0
        while x < w:
            c = px[y * w + x]
            start = x
            while x < w and px[y * w + x] == c and x - start < 0xFFFF:
                x += 1
            row.append((c, x - start))
        rows.append(row)
    out = bytearray()
    at = h
    table = []
    for row in rows:
        table.append(at)
        at += len(row)
    out += struct.pack(f"<{h}i", *table)
    for row in rows:
        for c, n in row:
            out += struct.pack("<HH", c, n)
    return bytes(out)


TEXMAP_SIZES = {64: 0x2000, 128: 0x8000}
TEXIDX_RECORD = 12


def texmap_pixels(path: Path) -> tuple[int, list[int]]:
    """A square texmap PNG as UO colour (every pixel kept, no transparency); size 0 if not 64 or 128 square."""
    from PIL import Image

    with Image.open(path) as im:
        im = im.convert("RGBA")
        w, h = im.size
        data = list(im.getdata())
    if w != h or w not in TEXMAP_SIZES:
        return 0, []
    return w, [to16(r, g, b, a, True) for r, g, b, a in data]


def encode_texmap(px: list[int]) -> bytes:
    return b"".join(struct.pack("<H", v) for v in px)


# --- decoders (mirror the loaders, independently of the encoders) ------------

def decode_land(raw: bytes) -> list[int]:
    px = [0] * (44 * 44)
    at = 0
    for y in range(44):
        for x in range(44):
            if in_diamond(x, y):
                px[y * 44 + x] = struct.unpack_from("<H", raw, at)[0]
                at += 2
    return px


def decode_texmap(raw: bytes) -> tuple[int, list[int]]:
    """TexmapsLoader.GetTexmap: 0x2000 bytes is 64x64, anything else 128x128."""
    size = 64 if len(raw) == 0x2000 else 128
    return size, list(struct.unpack_from(f"<{size * size}H", raw))


def decode_static(raw: bytes) -> tuple[int, int, list[int]]:
    """ArtLoader.LoadArt + Runs: header, row table, spans."""
    _, w, h = struct.unpack_from("<IHH", raw, 0)
    table = struct.unpack_from(f"<{h}H", raw, 8)
    base = 8 + h * 2
    px = [0] * (w * h)
    for y in range(h):
        p = base + table[y] * 2
        x = 0
        while True:
            gap, run = struct.unpack_from("<HH", raw, p)
            p += 4
            if gap + run >= 2048:
                raise ValueError(f"row {y}: span {gap}+{run} out of range")
            if gap + run == 0:
                break
            x += gap
            for j in range(run):
                px[y * w + x + j] = struct.unpack_from("<H", raw, p)[0]
                p += 2
            x += run
    return w, h, px


def decode_gump(raw: bytes, w: int, h: int) -> list[int]:
    """GumpsLoader.Decode: int32 row table in 4-byte units, then (colour, run)."""
    table = struct.unpack_from(f"<{h}i", raw, 0)
    half = len(raw) >> 2
    px = [0] * (w * h)
    for y in range(h):
        n = (table[y + 1] if y < h - 1 else half) - table[y]
        p = table[y] * 4
        x = 0
        for _ in range(n):
            c, run = struct.unpack_from("<HH", raw, p)
            p += 4
            for _ in range(run):
                px[y * w + x] = c
                x += 1
    return px


# --- the project's assets ------------------------------------------------------

@dataclass
class HueEntry:
    hue: int
    name: str
    colors: list[int]
    table_start: int
    table_end: int

    def pack(self) -> bytes:
        name = self.name.encode("ascii", "replace")[:20].ljust(20, b"\0")
        return struct.pack("<32H", *self.colors) + struct.pack("<HH", self.table_start, self.table_end) + name


def _ids(folder: Path, suffix: str) -> list[tuple[int, Path]]:
    if not folder.is_dir():
        return []
    out = []
    for f in folder.glob(f"*{suffix}"):
        stem = f.stem
        if stem.lower().startswith("0x"):
            try:
                out.append((int(stem[2:], 16), f))
            except ValueError:
                pass
    return sorted(out)


def project_assets(project: Path) -> dict[str, list[tuple[int, Path]]]:
    a = project / "assets"
    return {
        "land": _ids(a / "art" / "land", ".png"),
        "statics": _ids(a / "art" / "statics", ".png"),
        "gumps": _ids(a / "gumps", ".png"),
        "texmaps": _ids(a / "art" / "texmaps", ".png"),
        "hues": _ids(a / "hues", ".json"),
    }


def read_hue(path: Path) -> HueEntry:
    j = json.loads(path.read_text(encoding="utf-8"))
    return HueEntry(int(j["hue"]), j.get("name", ""), [int(c, 16) for c in j["colors"]],
                    int(j["table_start"], 16), int(j["table_end"], 16))


# --- verdata --------------------------------------------------------------------

@dataclass
class Patch:
    file_id: int
    block: int
    data: bytes
    extra: int = 0


def read_verdata(path: Path) -> list[Patch]:
    raw = path.read_bytes()
    (count,) = struct.unpack_from("<i", raw, 0)
    out = []
    for i in range(count):
        fid, block, pos, length, extra = struct.unpack_from("<5I", raw, 4 + i * VERDATA_RECORD)
        out.append(Patch(fid, block, raw[pos:pos + length], extra))
    return out


def write_verdata(path: Path, patches: list[Patch]) -> None:
    head = 4 + len(patches) * VERDATA_RECORD
    records = bytearray(struct.pack("<i", len(patches)))
    body = bytearray()
    for p in patches:
        records += struct.pack("<5I", p.file_id, p.block, head + len(body), len(p.data), p.extra)
        body += p.data
    # ArtLoader reads Length bytes after a static's 8-byte header: leave room.
    path.write_bytes(bytes(records) + bytes(body) + b"\0" * 16)
