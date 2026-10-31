"""Exact modern metadata records preserve padding and unrelated native bytes."""
import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.uorecord import AssetRecord
from uodata_write import uodata as U


def fixture(root):
    source = root / "source"; source.mkdir()
    value = bytearray((i * 13 + 77) & 255 for i in range(U.TILE_LAND_BYTES + 2 * U.TILE_STATIC_BLOCK))
    (source / "tiledata.mul").write_bytes(value)
    return source, bytes(value)


def test_raw_metadata_keeps_utf8_name_padding_unknown_fields_headers_and_install(tmp_path):
    source, original = fixture(tmp_path)
    stage = U.Stage(tmp_path / "stage", source)
    at = U.tile_offset(31)
    raw = bytearray(original[at:at + 41]); raw[:8] = struct.pack("<Q", 0xFEDCBA9876543210)
    raw[20] = 255; raw[21:] = "é raw".encode() + b"\0" + bytes(range(13))
    assert len(raw) == 41
    record = AssetRecord("tiledata-item", 31, bytes(raw))
    U.write_records(stage, [record], replace=True)
    expected = bytearray(original); expected[at:at + 41] = raw
    assert stage.read_path("tiledata.mul").read_bytes() == expected
    assert U.read_back_equal(stage, record)
    assert (source / "tiledata.mul").read_bytes() == original


@pytest.mark.parametrize("ident,length,meta,replace", [(64, 41, {}, True), (65536, 41, {}, True),
    (0, 40, {}, True), (0, 42, {}, True), (0, 41, {"height": 1}, True), (0, 41, {}, False)])
def test_raw_metadata_refused_before_copy(tmp_path, ident, length, meta, replace):
    source, _ = fixture(tmp_path)
    stage = U.Stage(tmp_path / "stage", source)
    with pytest.raises(ValueError):
        U.write_records(stage, [AssetRecord("tiledata-item", 1, bytes(41)),
            AssetRecord("tiledata-item", ident, bytes(length), meta)], replace=replace)
    assert not list(stage.root.iterdir())


@pytest.mark.parametrize("damage", ["old", "truncated"])
def test_raw_metadata_unsupported_layout_refused(tmp_path, damage):
    source, original = fixture(tmp_path)
    (source / "tiledata.mul").write_bytes(bytes(U.TILE_OLD_LAND_BYTES + U.TILE_OLD_STATIC_BLOCK) if damage == "old" else original[:-1])
    stage = U.Stage(tmp_path / "stage", source)
    with pytest.raises(ValueError): U.write_records(stage, [AssetRecord("tiledata-item", 1, bytes(41))], replace=True)
    assert not list(stage.root.iterdir())
