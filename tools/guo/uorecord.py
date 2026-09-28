"""The one asset record that tools/uopack produces and tools/uodata_write consumes (ADR-0022).

`data` is the encoded client payload, never a PNG:

  kind "static" / "land"   id = item / land id (not +0x4000); data = raw art as the client stores it
                           (static: 4-byte header, width, height, row table, runs; land: 2,024 bytes)
  kind "gump"              id = gump id; data = the UOP gump layout: uint32 width, uint32 height, RLE rows;
                           meta {"width", "height"}
  kind "anim"              id = body; meta {"action", "direction"}; data = the raw MUL payload for that
                           body/action/direction (palette, frame count, lookup table, frames)
  kind "tiledata-item"     id = item id; data = b""; meta = the fields to set (see uodata_write.TILE_FIELDS)
  kind "hue"               id = hue number (1-based); data = the 88-byte hues.mul block
  kind "multi"             id = multi id (below 0x4000); data = the MultiCollection.uop record:
                           uint32 id, int32 count, then per component uint16 item, int16 x, y, z,
                           uint16 flags (0 shown, 1 hidden), uint32 0

Sidecars (PNG paths and the rest) belong to tools/uopack; a record carries only what the
writers need.
"""
from __future__ import annotations

from dataclasses import dataclass, field

KINDS = {"static", "land", "gump", "anim", "tiledata-item", "hue", "multi"}


@dataclass
class AssetRecord:
    kind: str
    id: int
    data: bytes = b""
    meta: dict = field(default_factory=dict)

    def __post_init__(self) -> None:
        if self.kind not in KINDS:
            raise ValueError(f"unknown asset kind {self.kind!r}")
        if self.id < 0:
            raise ValueError(f"negative id for {self.kind}")
