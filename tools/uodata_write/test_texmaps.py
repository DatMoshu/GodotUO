"""Native texture replacement invariants, using synthetic opaque 16-bit records."""
import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.uorecord import AssetRecord
from uodata_write import uodata as U


def library(root):
    source = root / "source"
    source.mkdir()
    # Opaque high bit and zero must survive identity/revert without PNG conversion.
    first = struct.pack("<4H", 0, 0x8000, 0xffff, 0x1234) * 1024
    second = b"\x41\x82" * 16384
    (source / "texmaps.mul").write_bytes(first + second)
    (source / "texidx.mul").write_bytes(struct.pack("<9i", 0, len(first), -2147483648,
        len(first), len(second), 0x12345678, -1, -1, -1))
    return source, first, second


def test_texture_replace_preserves_prefix_extra_other_rows_and_install(tmp_path):
    source, first, second = library(tmp_path)
    before = {p.name: p.read_bytes() for p in source.iterdir()}
    stage = U.Stage(tmp_path / "stage", source)
    replacement = b"\x00\x80" * 16384
    record = AssetRecord("texmap", 0, replacement)
    U.write_records(stage, [record], replace=True)
    idx = stage.read_path("texidx.mul").read_bytes()
    mul = stage.read_path("texmaps.mul").read_bytes()
    assert mul == first + second + replacement
    assert struct.unpack_from("<iii", idx) == (len(first + second), len(replacement), -2147483648)
    assert idx[12:] == before["texidx.mul"][12:]
    assert U.read_back_equal(stage, record)
    assert before == {p.name: p.read_bytes() for p in source.iterdir()}


def test_identity_texture_does_not_append_or_clear_high_bits(tmp_path):
    source, first, _ = library(tmp_path)
    stage = U.Stage(tmp_path / "stage", source)
    U.write_records(stage, [AssetRecord("texmap", 0, first)], replace=True)
    for name in ("texidx.mul", "texmaps.mul"):
        assert stage.read_path(name).read_bytes() == (source / name).read_bytes()


@pytest.mark.parametrize("records,replace", [
    ([AssetRecord("texmap", 0, bytes(8192))], False),
    ([AssetRecord("texmap", 2, bytes(8192))], True),
    ([AssetRecord("texmap", 3, bytes(8192))], True),
    ([AssetRecord("texmap", 0, bytes(8191))], True),
    ([AssetRecord("texmap", 0, bytes(8192)), AssetRecord("texmap", 0, bytes(32768))], True),
    ([AssetRecord("texmap", 0, bytes(8192)), AssetRecord("texmap", 1, b"invalid")], True),
])
def test_texture_batch_refuses_before_any_copy(tmp_path, records, replace):
    source, _, _ = library(tmp_path)
    stage = U.Stage(tmp_path / "stage", source)
    with pytest.raises(ValueError):
        U.write_records(stage, records, replace=replace)
    assert not stage.root.exists() or not list(stage.root.iterdir())


@pytest.mark.parametrize("corruption", ["partial-index", "short-mul", "bad-length", "past-end"])
def test_corrupt_texture_source_refused(tmp_path, corruption):
    source, _, _ = library(tmp_path)
    idx, mul = source / "texidx.mul", source / "texmaps.mul"
    raw = bytearray(idx.read_bytes())
    if corruption == "partial-index":
        raw += b"x"
    elif corruption == "short-mul":
        mul.write_bytes(mul.read_bytes()[:-1])
    elif corruption == "bad-length":
        struct.pack_into("<i", raw, 16, 7)
    else:
        struct.pack_into("<i", raw, 12, 0x7fffffff)
    idx.write_bytes(raw)
    stage = U.Stage(tmp_path / "stage", source)
    with pytest.raises(ValueError):
        U.write_records(stage, [AssetRecord("texmap", 0, bytes(8192))], replace=True)
    assert not stage.root.exists() or not list(stage.root.iterdir())
