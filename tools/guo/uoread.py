"""Read raw entries out of a UO client's data files: MUL+IDX and LegacyMUL UOP.

Shared by tools/uopack (bulk unpack) and tools/uodata_write (staging, the
free-slot scan). It returns the bytes the client stores for an entry, not
decoded pixels; the codecs are in tools/guo/uoart.py and tools/uopack.

The layouts follow the ported loaders; keep them in step:
  IO/UOFileUop.cs          the UOP container and CreateHash (Bob Jenkins' hashlittle2)
  IO/UOFileMul.cs          MUL + IDX: 12-byte records (int32 position, int32 length, int32 extra)
  Assets/ArtLoader.cs      artLegacyMUL.uop "build/artlegacymul/{:08}.tga", or art.mul/artidx.mul;
                           land 0..0x3FFF, statics at 0x4000 + id
  Assets/GumpsLoader.cs    gumpartLegacyMUL.uop "build/gumpartlegacymul/{:08}.tga" with an 8-byte
                           width/height prefix, or gumpart.mul/gumpidx.mul (extra = width << 16 | height)
  Assets/AnimationsLoader.cs  anim.mul/anim.idx groups (MUL); anim2..5 via the body tables are not read here
  Assets/TileDataLoader.cs tiledata.mul, old (< 7.0.9, uint32 flags) and new (uint64 flags) layouts

Only reads. Nothing here writes to a data folder.
"""

from __future__ import annotations

import struct
import zlib
from dataclasses import dataclass
from pathlib import Path

UOP_MAGIC = 0x50594D
LAND_COUNT = 0x4000


def uop_hash(s: str) -> int:
    """UOFileUop.CreateHash: hashlittle2 over the lower-case entry name."""
    m = 0xFFFFFFFF
    b = s.encode("ascii")
    n = len(b)
    ebx = edi = esi = (n + 0xDEADBEEF) & m
    eax = ecx = edx = 0
    i = 0
    while i + 12 < n:
        edi = (((b[i + 7] << 24) | (b[i + 6] << 16) | (b[i + 5] << 8) | b[i + 4]) + edi) & m
        esi = (((b[i + 11] << 24) | (b[i + 10] << 16) | (b[i + 9] << 8) | b[i + 8]) + esi) & m
        edx = (((b[i + 3] << 24) | (b[i + 2] << 16) | (b[i + 1] << 8) | b[i]) - esi) & m
        edx = ((edx + ebx) & m) ^ (esi >> 28) ^ ((esi << 4) & m)
        esi = (esi + edi) & m
        edi = ((edi - edx) & m) ^ (edx >> 26) ^ ((edx << 6) & m)
        edx = (edx + esi) & m
        esi = ((esi - edi) & m) ^ (edi >> 24) ^ ((edi << 8) & m)
        edi = (edi + edx) & m
        ebx = ((edx - esi) & m) ^ (esi >> 16) ^ ((esi << 16) & m)
        esi = (esi + edi) & m
        edi = ((edi - ebx) & m) ^ (ebx >> 13) ^ ((ebx << 19) & m)
        ebx = (ebx + esi) & m
        esi = ((esi - edi) & m) ^ (edi >> 28) ^ ((edi << 4) & m)
        edi = (edi + ebx) & m
        i += 12
    rest = n - i
    if rest <= 0:
        return (esi << 32) | eax
    # The C#'s fall-through switch: bytes 0-3 of the tail go to ebx, 4-7 to
    # edi, 8-11 to esi, each shifted by its place in the word.
    words = [ebx, edi, esi]
    for k in range(rest - 1, -1, -1):
        words[k // 4] = (words[k // 4] + (b[i + k] << (8 * (k % 4)))) & m
    ebx, edi, esi = words
    esi = ((esi ^ edi) - ((edi >> 18) ^ ((edi << 14) & m))) & m
    ecx = ((esi ^ ebx) - ((esi >> 21) ^ ((esi << 11) & m))) & m
    edi = ((edi ^ ecx) - ((ecx >> 7) ^ ((ecx << 25) & m))) & m
    esi = ((esi ^ edi) - ((edi >> 16) ^ ((edi << 16) & m))) & m
    edx = ((esi ^ ecx) - ((esi >> 28) ^ ((esi << 4) & m))) & m
    edi = ((edi ^ edx) - ((edx >> 18) ^ ((edx << 14) & m))) & m
    eax = ((esi ^ edi) - ((edi >> 8) ^ ((edi << 24) & m))) & m
    return (edi << 32) | eax


def bwt_decompress(buf: bytes) -> bytes:
    """Utility/BwtDecompress.cs, line for line: a move-to-front pass, then the
    inverse transform. Used after zlib on UOP entries flagged 3 (ZlibBwt), which
    is how modern installs store every gump."""
    # Stage 1. BuildTable sorts all 65,536 (first + second << 8) values, so the
    # table is simply 0..65535; only its first 256 slots ever move.
    table = list(range(256))
    out1 = bytearray(len(buf) - 4)
    i = 0
    pos = 4
    cur = buf[pos]
    pos += 1
    # As the C#: the loop tests before each step, so the last byte is read
    # but never decoded.
    while pos < len(buf):
        value = table[cur]
        if cur > 0:
            table[1:cur + 1] = table[0:cur]
        table[0] = value
        out1[i] = value & 0xFF
        i += 1
        cur = buf[pos]
        pos += 1
    return _bwt_internal(out1)


def _bwt_internal(inp: bytearray) -> bytes:
    counts = list(struct.unpack_from("<256i", inp, 0))
    total = sum(counts)
    symbols = list(range(256))
    nonzero = sum(1 for c in counts if c)
    # Frequency(): symbols by count, highest first; ties go to the lowest symbol.
    order = sorted((s for s in range(256) if counts[s]), key=lambda s: (-counts[s], s))
    first = [0] * 256
    last = [0] * 256
    m = 0
    for k in range(nonzero):
        freq = order[k]
        symbols[inp[m + 1024]] = freq
        first[freq] = m + 1
        m += counts[freq]
        last[freq] = m
    out = bytearray(total)
    val = symbols[0]
    count = 0
    while count < total:
        out[count] = val
        if first[val] >= last[val]:
            if nonzero > 0:
                nonzero -= 1
                symbols[0:nonzero] = symbols[1:nonzero + 1]
                val = symbols[0]
            else:
                nonzero -= 1
        else:
            idx = inp[first[val] + 1024]
            first[val] += 1
            if idx != 0:
                symbols[0:idx] = symbols[1:idx + 1]
                symbols[idx] = val
                val = symbols[0]
        count += 1
    return bytes(out)


@dataclass
class UopEntry:
    offset: int          # of the data, past the entry header
    compressed: int
    decompressed: int
    flag: int            # 0 stored, 1 zlib


class UopFile:
    """A UOP archive: entry name hash -> where its bytes are."""

    def __init__(self, path: Path):
        self.path = Path(path)
        self.entries: dict[int, UopEntry] = {}
        with open(self.path, "rb") as f:
            magic, _version, _stamp, next_block, _block_size, _count = struct.unpack("<IIIqIi", f.read(28))
            if magic != UOP_MAGIC:
                raise ValueError(f"{path}: not a UOP file")
            while next_block:
                f.seek(next_block)
                files, next_block = struct.unpack("<iq", f.read(12))
                for _ in range(files):
                    offset, header, comp, decomp, h, _dh, flag = struct.unpack("<qiiiQIh", f.read(34))
                    if offset == 0:
                        continue
                    self.entries[h] = UopEntry(offset + header, comp, decomp, flag)

    def read(self, name: str) -> bytes | None:
        e = self.entries.get(uop_hash(name))
        if e is None:
            return None
        with open(self.path, "rb") as f:
            f.seek(e.offset)
            raw = f.read(e.compressed)
        if e.flag == 0:
            return raw
        data = zlib.decompress(raw)
        return bwt_decompress(data) if e.flag == 3 else data


class MulIndex:
    """A MUL data file and its IDX: 12-byte records (position, length, extra)."""

    def __init__(self, mul: Path, idx: Path):
        self.mul = Path(mul)
        data = Path(idx).read_bytes()
        self.records = [struct.unpack_from("<iii", data, i) for i in range(0, len(data) - 11, 12)]

    def entry(self, index: int) -> tuple[bytes, int] | None:
        if not 0 <= index < len(self.records):
            return None
        pos, length, extra = self.records[index]
        if pos < 0 or length <= 0 or pos == 0xFFFFFFFF:
            return None
        with open(self.mul, "rb") as f:
            f.seek(pos)
            return f.read(length), extra


def _find(data_dir: Path, name: str) -> Path | None:
    """Case-insensitive lookup, as installs differ in case."""
    p = data_dir / name
    if p.exists():
        return p
    low = name.lower()
    for f in data_dir.iterdir():
        if f.name.lower() == low:
            return f
    return None


class Art:
    """Land (0..0x3FFF) and static art. get_land(id) / get_static(id) return the raw entry."""

    def __init__(self, data_dir: Path):
        data_dir = Path(data_dir)
        uop = _find(data_dir, "artLegacyMUL.uop")
        self.uop = UopFile(uop) if uop else None
        self.mul = None
        if not self.uop:
            mul, idx = _find(data_dir, "art.mul"), _find(data_dir, "artidx.mul")
            if not (mul and idx):
                raise FileNotFoundError(f"no art in {data_dir}")
            self.mul = MulIndex(mul, idx)

    def _raw(self, index: int) -> bytes | None:
        if self.uop:
            return self.uop.read(f"build/artlegacymul/{index:08d}.tga")
        e = self.mul.entry(index)
        return e[0] if e else None

    def get_land(self, land_id: int) -> bytes | None:
        return self._raw(land_id)

    def get_static(self, item_id: int) -> bytes | None:
        return self._raw(LAND_COUNT + item_id)


class Gumps:
    """get(id) -> (width, height, rle rows) with the size header stripped, or None."""

    def __init__(self, data_dir: Path):
        data_dir = Path(data_dir)
        uop = _find(data_dir, "gumpartLegacyMUL.uop")
        self.uop = UopFile(uop) if uop else None
        self.mul = None
        if not self.uop:
            mul, idx = _find(data_dir, "gumpart.mul"), _find(data_dir, "gumpidx.mul")
            if not (mul and idx):
                raise FileNotFoundError(f"no gumps in {data_dir}")
            self.mul = MulIndex(mul, idx)

    def get(self, gump_id: int) -> tuple[int, int, bytes] | None:
        if self.uop:
            raw = self.uop.read(f"build/gumpartlegacymul/{gump_id:08d}.tga")
            if raw is None or len(raw) < 8:
                return None
            w, h = struct.unpack_from("<ii", raw, 0)
            return w, h, raw[8:]
        e = self.mul.entry(gump_id)
        if e is None:
            return None
        raw, extra = e
        return (extra >> 16) & 0xFFFF, extra & 0xFFFF, raw


def anim_group_index(body: int, action: int, direction: int) -> int:
    """anim.idx record of body/action/direction (AnimationsLoader, anim.mul):
    bodies < 200 have 22 actions, < 400 have 13, the rest 35; five stored directions."""
    if body < 200:
        base, actions = body * 110, 22
    elif body < 400:
        base, actions = 22000 + (body - 200) * 65, 13
    else:
        base, actions = 35000 + (body - 400) * 175, 35
    if not 0 <= action < actions or not 0 <= direction < 5:
        raise ValueError(f"body {body} has actions 0..{actions - 1}, directions 0..4")
    return base + action * 5 + direction


def anim_actions(body: int) -> int:
    return 22 if body < 200 else 13 if body < 400 else 35


class Anim:
    """anim.mul groups: get(body, action, direction) -> the raw payload
    (512-byte palette, uint32 frame count, uint32 offsets, frames)."""

    def __init__(self, data_dir: Path, file: str = "anim"):
        data_dir = Path(data_dir)
        mul, idx = _find(data_dir, f"{file}.mul"), _find(data_dir, f"{file}.idx")
        if not (mul and idx):
            raise FileNotFoundError(f"no {file}.mul/{file}.idx in {data_dir}")
        self.mul = MulIndex(mul, idx)

    def get(self, body: int, action: int, direction: int) -> bytes | None:
        e = self.mul.entry(anim_group_index(body, action, direction))
        return e[0] if e else None


STATIC_FIELDS = ("flags", "weight", "layer", "count", "anim", "hue", "light", "height", "name")


class TileData:
    """tiledata.mul: land and static entries as dicts. Old or new layout by file size."""

    def __init__(self, data_dir: Path):
        path = _find(Path(data_dir), "tiledata.mul")
        if not path:
            raise FileNotFoundError(f"no tiledata.mul in {data_dir}")
        self.raw = path.read_bytes()
        n = len(self.raw)
        new_land, old_land = 512 * (4 + 32 * 30), 512 * (4 + 32 * 26)
        if n > new_land and (n - new_land) % (4 + 32 * 41) == 0:
            self.new = True
        elif n > old_land and (n - old_land) % (4 + 32 * 37) == 0:
            self.new = False
        else:
            raise ValueError(f"{path}: unrecognised tiledata size {n}")
        self.land_size = 30 if self.new else 26
        self.static_size = 41 if self.new else 37
        self.static_base = new_land if self.new else old_land
        self.static_count = (n - self.static_base) // (4 + 32 * self.static_size) * 32

    def static(self, item_id: int) -> dict | None:
        if not 0 <= item_id < self.static_count:
            return None
        group, j = divmod(item_id, 32)
        p = self.static_base + group * (4 + 32 * self.static_size) + 4 + j * self.static_size
        if self.new:
            flags = struct.unpack_from("<Q", self.raw, p)[0]
            p += 8
        else:
            flags = struct.unpack_from("<I", self.raw, p)[0]
            p += 4
        weight, layer, count, anim, hue, light, height = struct.unpack_from("<BBiHHHB", self.raw, p)
        name = self.raw[p + 13:p + 33].split(b"\0", 1)[0].decode("latin-1")
        return {"flags": flags, "weight": weight, "layer": layer, "count": count, "anim": anim,
                "hue": hue, "light": light, "height": height, "name": name}

    def land(self, land_id: int) -> dict | None:
        if not 0 <= land_id < LAND_COUNT:
            return None
        group, j = divmod(land_id, 32)
        p = group * (4 + 32 * self.land_size) + 4 + j * self.land_size
        if self.new:
            flags = struct.unpack_from("<Q", self.raw, p)[0]
            p += 8
        else:
            flags = struct.unpack_from("<I", self.raw, p)[0]
            p += 4
        tex = struct.unpack_from("<H", self.raw, p)[0]
        name = self.raw[p + 2:p + 22].split(b"\0", 1)[0].decode("latin-1")
        return {"flags": flags, "texture": tex, "name": name}
