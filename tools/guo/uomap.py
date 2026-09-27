"""Read UO map and statics files, the way the port's MapLoader does.

Shared by tools/world (export, verify) and anything else that needs map
blocks without the Godot runtime. Read only: nothing here opens a file for
writing. The layouts follow godot/GUO/src/Assets/MapLoader.cs and
src/IO/UOFileUop.cs; keep them in step with those files.

A facet's land is ``map<N>LegacyMUL.uop`` (or ``map<N>.mul``): 196-byte
blocks (a 4-byte header, then 64 cells of ``ushort id, sbyte z``), block
number ``bx * height_in_blocks + by``. In the UOP, entry ``n`` (hashed from
``build/map<N>legacymul/<n:08d>.dat``) holds blocks ``n*4096 .. n*4096+4095``.
Statics are ``staidx<N>.mul`` (12 bytes per block: int32 offset, int32 length,
int32 extra) into ``statics<N>.mul`` (7 bytes per static: ushort id, byte x,
byte y, sbyte z, ushort hue).
"""

from __future__ import annotations

import hashlib
import mmap
import struct
from dataclasses import dataclass, field
from pathlib import Path

MAP_BLOCK = 196
STATIC_SIZE = 7
IDX_SIZE = 12
UOP_MAGIC = 0x50594D
BLOCKS_PER_UOP_ENTRY = 4096

# MapLoader.MapsDefaultSize, in cells. map0/map1 are 6144 wide on very old
# installs (393216 blocks); load_facet detects that the same way the loader does.
FACET_SIZE = {0: (7168, 4096), 1: (7168, 4096), 2: (2304, 1600), 3: (2560, 2048), 4: (1448, 1448), 5: (1280, 4096)}


def uop_hash(s: str) -> int:
    """UOFileUop.CreateHash: Bob Jenkins' hashlittle2 as the UO client uses it."""
    M = 0xFFFFFFFF
    eax = ecx = edx = 0
    ebx = edi = esi = (len(s) + 0xDEADBEEF) & M
    b = [ord(c) for c in s]
    i = 0
    while i + 12 < len(b):
        edi = ((b[i + 7] << 24) | (b[i + 6] << 16) | (b[i + 5] << 8) | b[i + 4]) + edi & M
        esi = ((b[i + 11] << 24) | (b[i + 10] << 16) | (b[i + 9] << 8) | b[i + 8]) + esi & M
        edx = ((b[i + 3] << 24) | (b[i + 2] << 16) | (b[i + 1] << 8) | b[i]) - esi & M
        edx = ((edx + ebx) & M) ^ (esi >> 28) ^ (esi << 4 & M)
        esi = esi + edi & M
        edi = ((edi - edx) & M) ^ (edx >> 26) ^ (edx << 6 & M)
        edx = edx + esi & M
        esi = ((esi - edi) & M) ^ (edi >> 24) ^ (edi << 8 & M)
        edi = edi + edx & M
        ebx = ((edx - esi) & M) ^ (esi >> 16) ^ (esi << 16 & M)
        esi = esi + edi & M
        edi = ((edi - ebx) & M) ^ (ebx >> 13) ^ (ebx << 19 & M)
        ebx = ebx + esi & M
        esi = ((esi - edi) & M) ^ (edi >> 28) ^ (edi << 4 & M)
        edi = edi + ebx & M
        i += 12

    rest = len(b) - i
    if rest > 0:
        if rest >= 12: esi = esi + (b[i + 11] << 24) & M
        if rest >= 11: esi = esi + (b[i + 10] << 16) & M
        if rest >= 10: esi = esi + (b[i + 9] << 8) & M
        if rest >= 9: esi = esi + b[i + 8] & M
        if rest >= 8: edi = edi + (b[i + 7] << 24) & M
        if rest >= 7: edi = edi + (b[i + 6] << 16) & M
        if rest >= 6: edi = edi + (b[i + 5] << 8) & M
        if rest >= 5: edi = edi + b[i + 4] & M
        if rest >= 4: ebx = ebx + (b[i + 3] << 24) & M
        if rest >= 3: ebx = ebx + (b[i + 2] << 16) & M
        if rest >= 2: ebx = ebx + (b[i + 1] << 8) & M
        if rest >= 1: ebx = ebx + b[i] & M

        esi = ((esi ^ edi) - ((edi >> 18) ^ (edi << 14 & M))) & M
        ecx = ((esi ^ ebx) - ((esi >> 21) ^ (esi << 11 & M))) & M
        edi = ((edi ^ ecx) - ((ecx >> 7) ^ (ecx << 25 & M))) & M
        esi = ((esi ^ edi) - ((edi >> 16) ^ (edi << 16 & M))) & M
        edx = ((esi ^ ecx) - ((esi >> 28) ^ (esi << 4 & M))) & M
        edi = ((edi ^ edx) - ((edx >> 18) ^ (edx << 14 & M))) & M
        eax = ((esi ^ edi) - ((edi >> 8) ^ (edi << 24 & M))) & M
        return (edi << 32) | eax

    return (esi << 32) | eax


def uop_entries(data: bytes | mmap.mmap) -> dict[int, tuple[int, int]]:
    """hash -> (data offset, length) for every entry, as UOFileUop.FillEntries reads them."""
    magic, _version, _stamp, next_block, _block_size, _count = struct.unpack_from("<IIIqIi", data, 0)
    if magic != UOP_MAGIC:
        raise ValueError("not a UOP file")
    entries: dict[int, tuple[int, int]] = {}
    while next_block:
        files, next_block_after = struct.unpack_from("<iq", data, next_block)
        pos = next_block + 12
        for _ in range(files):
            offset, header, compressed, decompressed, h, _adler, flag = struct.unpack_from("<qiiiQIh", data, pos)
            pos += 34
            if offset == 0:
                continue
            entries[h] = (offset + header, compressed if flag == 1 else decompressed)
        next_block = next_block_after
    return entries


@dataclass
class Block:
    """One map block: 64 land cells (index y * 8 + x) and its statics."""

    land_id: list[int] = field(default_factory=lambda: [0] * 64)
    land_z: list[int] = field(default_factory=lambda: [0] * 64)
    statics: list[tuple[int, int, int, int, int]] = field(default_factory=list)  # id, x, y, z, hue


class Facet:
    """One facet's land and statics files, memory-mapped read only."""

    def __init__(self, map_path: Path, idx_path: Path, statics_path: Path, facet: int):
        self.facet = facet
        self.map_path, self.idx_path, self.statics_path = map_path, idx_path, statics_path
        self._files = [open(p, "rb") for p in (map_path, idx_path, statics_path)]
        self.map, self.idx, self.sta = (mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) for f in self._files)
        self.is_uop = map_path.suffix.lower() == ".uop"
        self._uop_offsets: list[int] = []
        if self.is_uop:
            entries = uop_entries(self.map)
            n = 0
            while True:
                e = entries.get(uop_hash(f"build/map{facet}legacymul/{n:08d}.dat"))
                if e is None:
                    break
                self._uop_offsets.append(e[0])
                n += 1
        width, height = FACET_SIZE[facet]
        if facet in (0, 1) and self.block_count() == 393216:
            width = 6144
        self.width_blocks, self.height_blocks = width >> 3, height >> 3

    def close(self) -> None:
        for m in (self.map, self.idx, self.sta):
            m.close()
        for f in self._files:
            f.close()

    def __enter__(self) -> "Facet":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def block_count(self) -> int:
        if self.is_uop:
            return len(self._uop_offsets) * BLOCKS_PER_UOP_ENTRY
        return len(self.map) // MAP_BLOCK

    def number(self, bx: int, by: int) -> int:
        return bx * self.height_blocks + by

    def map_offset(self, number: int) -> int:
        """Where a block's 196 bytes start in the map file (MapLoader.LoadMap's arithmetic)."""
        if self.is_uop:
            return self._uop_offsets[number >> 12] + (number & 4095) * MAP_BLOCK
        return number * MAP_BLOCK

    def statics_span(self, number: int) -> tuple[int, int]:
        """(offset, length) of a block's statics; length 0 when it has none."""
        offset, length, _extra = struct.unpack_from("<iii", self.idx, number * IDX_SIZE)
        if length <= 0 or offset < 0 or offset == -1:
            return 0, 0
        return offset, length

    def read(self, bx: int, by: int) -> Block:
        n = self.number(bx, by)
        at = self.map_offset(n) + 4
        b = Block()
        for i in range(64):
            b.land_id[i], b.land_z[i] = struct.unpack_from("<Hb", self.map, at + i * 3)
        offset, length = self.statics_span(n)
        for k in range(length // STATIC_SIZE):
            sid, x, y, z, hue = struct.unpack_from("<HBBbH", self.sta, offset + k * STATIC_SIZE)
            if sid in (0, 0xFFFF):
                continue
            b.statics.append((sid, x, y, z, hue))
        return b


def facet_files(data_dir: Path, facet: int) -> tuple[Path, Path, Path]:
    """The land, index and statics files a client reads for a facet (UOP land preferred, as MapLoader does)."""
    uop = data_dir / f"map{facet}LegacyMUL.uop"
    land = uop if uop.exists() else data_dir / f"map{facet}.mul"
    return land, data_dir / f"staidx{facet}.mul", data_dir / f"statics{facet}.mul"


def open_facet(data_dir: Path, facet: int) -> Facet:
    return Facet(*facet_files(data_dir, facet), facet=facet)


def install_fingerprint(data_dir: Path) -> str:
    """WorldProject.Fingerprint: SHA-1 over sorted 'name:size' of the map/statics/staidx files."""
    names = [p for p in data_dir.iterdir() if p.is_file() and p.name.lower().startswith(("map", "statics", "staidx"))]
    text = "".join(f"{p.name.lower()}:{p.stat().st_size}\n" for p in sorted(names, key=lambda p: str(p).upper()))
    return hashlib.sha1(text.encode("utf-8")).hexdigest()
