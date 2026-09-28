"""Multi records: read every multi an install holds, and encode new ones.

Two containers, as MultiLoader (src/Assets) and ModernUO's MultiData read them:

  MultiCollection.uop  entry "build/multicollection/{id:06d}.bin", maybe zlib:
                       uint32 id, int32 count, then per component
                       uint16 item, int16 x, y, z, uint16 flags, uint32 n, n * uint32
  multi.mul / .idx     per component uint16 item, int16 x, y, z, uint32 flags
                       (+ uint32 unknown from client 7.0.9.0 on: 16 bytes a record)

Component flags, UOP form: 0 visible (the usual), 1 invisible, 0x101 visible
"generic". MUL form: 0 invisible, anything else visible. `Component.visible`
is the one meaning both share.

ModernUO reads an entry into a 64 KB buffer, so one multi holds at most
MAX_COMPONENTS components; bigger buildings are split into several multis.
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.uoread import MulIndex, UopFile, _find  # noqa: E402

UOP_NAME = "build/multicollection/{0:06d}.bin"
HOUSING_NAME = "build/multicollection/housing.bin"
MULTI_ID_LIMIT = 0x4000                # ModernUO masks multi ids with 0x3FFF
UOP_RECORD = 14                         # without the cliloc list
MAX_COMPONENTS = (0x10000 - 8) // UOP_RECORD


@dataclass(frozen=True)
class Component:
    item: int
    x: int
    y: int
    z: int
    visible: bool = True

    def as_list(self) -> list:
        return [self.item, self.x, self.y, self.z] + ([] if self.visible else [0])


def decode_uop(data: bytes) -> list[Component]:
    _id, count = struct.unpack_from("<Ii", data, 0)
    out, p = [], 8
    for _ in range(count):
        item, x, y, z, flags, n = struct.unpack_from("<HhhhHI", data, p)
        p += UOP_RECORD + 4 * n
        out.append(Component(item, x, y, z, flags in (0, 0x100, 0x101)))
    return out


def encode_uop(multi_id: int, comps: list[Component]) -> bytes:
    if len(comps) > MAX_COMPONENTS:
        raise ValueError(f"multi {multi_id:#x}: {len(comps)} components, the shard reads at most {MAX_COMPONENTS}")
    out = bytearray(struct.pack("<Ii", multi_id, len(comps)))
    for c in comps:
        out += struct.pack("<HhhhHI", c.item, c.x, c.y, c.z, 0 if c.visible else 1, 0)
    return bytes(out)


def decode_mul(data: bytes, record: int) -> list[Component]:
    out = []
    for p in range(0, len(data) - record + 1, record):
        item, x, y, z, flags = struct.unpack_from("<HhhhI", data, p)
        out.append(Component(item, x, y, z, flags != 0))
    return out


def encode_mul(comps: list[Component], record: int = 16) -> bytes:
    out = bytearray()
    for c in comps:
        out += struct.pack("<HhhhI", c.item, c.x, c.y, c.z, 1 if c.visible else 0) + b"\0" * (record - 12)
    return bytes(out)


class Multis:
    """Every multi in a data folder (a staged set or an install): get(id), ids()."""

    def __init__(self, data_dir: Path, prefer_uop: bool = True):
        data_dir = Path(data_dir)
        uop = _find(data_dir, "MultiCollection.uop") if prefer_uop else None
        self.uop = UopFile(uop) if uop else None
        self.mul = None
        self.record = 16
        if not self.uop:
            mul, idx = _find(data_dir, "multi.mul"), _find(data_dir, "multi.idx")
            if not (mul and idx):
                raise FileNotFoundError(f"no multis in {data_dir}")
            self.mul = MulIndex(mul, idx)
        self.source = "uop" if self.uop else "mul"

    def raw(self, multi_id: int) -> bytes | None:
        if self.uop:
            return self.uop.read(UOP_NAME.format(multi_id))
        e = self.mul.entry(multi_id)
        return e[0] if e else None

    def get(self, multi_id: int) -> list[Component] | None:
        data = self.raw(multi_id)
        if not data:
            return None
        return decode_uop(data) if self.uop else decode_mul(data, self.record)

    def ids(self) -> list[int]:
        if self.uop:
            from guo.uoread import uop_hash
            return [i for i in range(MULTI_ID_LIMIT) if uop_hash(UOP_NAME.format(i)) in self.uop.entries]
        return [i for i in range(len(self.mul.records)) if self.mul.entry(i)]


def bounds(comps: list[Component]) -> tuple[int, int, int, int]:
    xs, ys = [c.x for c in comps], [c.y for c in comps]
    return min(xs), min(ys), max(xs), max(ys)

