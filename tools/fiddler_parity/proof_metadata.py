"""Literal modern tiledata, pinned ClassicUO read-back, corruption and recovery."""
import base64
import json
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import metadata
import replacement
import run as parity


def record(seed):
    return struct.pack("<QBBiHHHB", 0xFEDCBA9876543210 + seed, 247, 19, -12345, 0x8123, 0xfdec, 0x9abc, 255) + b"old\0" + bytes(range(16))


def fixture(root, ident=31):
    source = root / "source"; source.mkdir()
    value = bytearray(metadata.writer.TILE_LAND_BYTES + 2 * metadata.writer.TILE_STATIC_BLOCK)
    for group in range(2):
        struct.pack_into("<I", value, metadata.writer.TILE_LAND_BYTES + group * metadata.writer.TILE_STATIC_BLOCK, 0xfedc1234 + group)
    for i in range(64):
        at = metadata.writer.tile_offset(i); value[at:at + 41] = record(i)
    (source / "tiledata.mul").write_bytes(value)
    before = record(ident); changed = bytearray(before)
    changed[:8] = struct.pack("<Q", 0x8000000004006050); changed[20] = 0
    changed[21:] = "é stone".encode().ljust(20, b"\0")
    edits = root / "edits.json"
    edits.write_text(json.dumps({"schema": 1, "domain": "item-metadata", "records": [{"id": ident,
        "source_record_base64": base64.b64encode(before).decode(), "record_base64": base64.b64encode(changed).decode()}]}))
    return source, root / "stage", edits, root / "plan", bytes(value), bytes(changed)


@pytest.fixture(scope="session")
def oracle():
    path = parity.default_oracle(); parity.doctor(path); return path


def prepared(root, oracle, **kwargs):
    source, stage, edits, plan, original, changed = fixture(root, **kwargs)
    spec = metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    return source, stage, edits, plan, original, changed, spec


@pytest.mark.parametrize("ident", [0, 31, 32, 63])
def test_native_item_group_boundaries_flags_utf8_preservation_recovery(tmp_path, oracle, ident):
    source, stage, edits, plan, original, changed, spec = prepared(tmp_path, oracle, ident=ident)
    report = json.loads((plan / "verification/report.json").read_text())
    assert report["outcome"] == "pass" and report["preserved_group_headers"] == 514
    assert report["fields"]["flags"] == 0x8000000004006050 and report["fields"]["name"] == "é stone"
    assert report["preserved_item_records"] == 63 and not stage.exists()
    metadata.apply(plan / "plan.json", oracle)
    expected = bytearray(original); at = metadata.writer.tile_offset(ident); expected[at:at + 41] = changed
    assert (stage / "tiledata.mul").read_bytes() == expected
    assert (source / "tiledata.mul").read_bytes() == original
    replacement.recover(stage); assert not stage.exists()


def test_staged_unexposed_fields_and_unrelated_content_are_preserved(tmp_path, oracle):
    source, stage, edits, plan, original, changed = fixture(tmp_path)
    staged = metadata.writer.Stage(stage, source); path = staged.path("tiledata.mul"); staged.save()
    current = bytearray(path.read_bytes()); at = metadata.writer.tile_offset(31)
    current[at + 8:at + 20] = bytes(range(12)); path.write_bytes(current)
    (stage / "notes.txt").write_bytes(b"unrelated user data")
    prior = replacement.stage_hashes(stage)
    spec = metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    merged = bytearray(changed); merged[8:20] = bytes(range(12))
    assert metadata.raw(spec["records"][0]["after_base64"]) == merged
    metadata.apply(plan / "plan.json", oracle)
    assert metadata.writer.read_tile_record(stage / "tiledata.mul", 31) == merged
    assert (stage / "notes.txt").read_bytes() == b"unrelated user data"
    replacement.recover(stage); assert replacement.stage_hashes(stage) == prior


def test_unchanged_opaque_name_bytes_are_not_transcoded(tmp_path, oracle):
    source, stage, edits, plan, original, changed = fixture(tmp_path)
    spec = json.loads(edits.read_text()); desired = bytearray(record(31)); desired[20] = 7
    spec["records"][0]["record_base64"] = base64.b64encode(desired).decode(); edits.write_text(json.dumps(spec))
    metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle); metadata.apply(plan / "plan.json", oracle)
    assert metadata.writer.read_tile_record(stage / "tiledata.mul", 31) == desired


def test_source_change_between_read_and_hash_refused(tmp_path, oracle, monkeypatch):
    source, stage, edits, plan, original, changed = fixture(tmp_path)
    capture = metadata.source_hashes
    def changed_source(root):
        path = root / "tiledata.mul"
        value = bytearray(path.read_bytes()); value[0] ^= 1; path.write_bytes(value)
        return capture(root)
    monkeypatch.setattr(metadata, "source_hashes", changed_source)
    with pytest.raises(ValueError, match="while capturing"):
        metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    assert not stage.exists() and not plan.exists()


@pytest.mark.parametrize("damage", ["flags", "height", "padding", "unknown", "other-item", "group-header", "land", "truncate", "append"])
def test_metadata_corruption_refused(tmp_path, oracle, damage):
    source, stage, edits, plan, original, changed, spec = prepared(tmp_path, oracle)
    path = plan / "candidate/tiledata.mul"; value = bytearray(path.read_bytes()); at = metadata.writer.tile_offset(31)
    if damage == "truncate": value.pop()
    elif damage == "append": value += b"x"
    else:
        where = {"flags": at + 7, "height": at + 20, "padding": at + 40, "unknown": at + 8,
            "other-item": metadata.writer.tile_offset(32), "group-header": metadata.writer.TILE_LAND_BYTES, "land": 0}[damage]
        value[where] ^= 1
    path.write_bytes(value)
    report = metadata.verify(plan / "baseline", plan / "candidate", plan / "expected.json", tmp_path / "bad-proof", oracle)
    assert report["outcome"] == "error"
    with pytest.raises(ValueError): metadata.apply(plan / "plan.json", oracle)
    assert not stage.exists()


@pytest.mark.parametrize("change", ["source", "new-verdata", "edits", "manifest", "baseline", "stage"])
def test_stale_metadata_review_refused(tmp_path, oracle, change):
    source, stage, edits, plan, original, changed, spec = prepared(tmp_path, oracle)
    if change == "stage": stage.mkdir(); (stage / "external.txt").write_text("new stage")
    elif change == "new-verdata": (source / "verdata.mul").write_bytes(struct.pack("<i", 0))
    else:
        path = {"source": source / "tiledata.mul", "edits": edits, "manifest": plan / "expected.json", "baseline": plan / "baseline/tiledata.mul"}[change]
        path.write_bytes(path.read_bytes() + b" ")
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError): metadata.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("kind,block,length,refused", [(4, 0x200, 1188, False), (30, 0x201, 1188, False), (30, 0x200, 1188, True)])
def test_winning_metadata_patch_refused_unrelated_patches_retained(tmp_path, oracle, kind, block, length, refused):
    source, stage, edits, plan, original, changed = fixture(tmp_path)
    patch = struct.pack("<6i", 1, kind, block, 24, length, 0) + bytes(length)
    (source / "verdata.mul").write_bytes(patch)
    if refused:
        with pytest.raises(ValueError, match="overrides"): metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
        assert not stage.exists()
    else:
        metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle); metadata.apply(plan / "plan.json", oracle)
        assert (source / "verdata.mul").read_bytes() == patch


@pytest.mark.parametrize("edit", ["unknown-field", "invalid-utf8", "name-padding", "wrong-source"])
def test_invalid_metadata_edit_refused_before_plan(tmp_path, oracle, edit):
    source, stage, edits, plan, original, changed = fixture(tmp_path)
    spec = json.loads(edits.read_text()); desired = bytearray(changed)
    if edit == "unknown-field": desired[8] ^= 1
    elif edit == "invalid-utf8": desired[21] = 255
    elif edit == "name-padding": desired[40] = 1
    else: spec["records"][0]["source_record_base64"] = base64.b64encode(record(32)).decode()
    spec["records"][0]["record_base64"] = base64.b64encode(desired).decode(); edits.write_text(json.dumps(spec))
    with pytest.raises(ValueError): metadata.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    assert not stage.exists() and not plan.exists()
