"""Opaque UOP entry headers must survive writes, even though the reader skips them."""
import hashlib
import json
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import batch
import replacement
import run as parity
from proof_static import art, make_uop, oracle


def fixture(root):
    source = root / "source"; source.mkdir()
    headers = {0x4001: bytes(range(135)), 0x4002: b"opaque-prefix:" + bytes(range(122)),
        0x4003: b"untouched-prefix" + bytes(range(121))}
    entries = {0x4001: art((0x001f,) * 6), 0x4002: art((0x03e0,) * 6), 0x4003: art()}
    make_uop(source / "artLegacyMUL.uop", entries, headers)
    images = root / "images"; images.mkdir()
    from PIL import Image
    Image.new("RGBA", (3, 2), (255, 0, 0, 255)).save(images / "1.png")
    Image.new("RGBA", (3, 2), (0, 255, 255, 255)).save(images / "2.png")
    return source, images, root / "stage", headers


def prepare(root, oracle):
    source, images, stage, headers = fixture(root)
    preview = batch.scan(images, source, stage)
    assert preview["ready"]
    scan_file = root / "scan.json"; scan_file.write_text(json.dumps(preview))
    plan = root / "plan"
    batch.prepare(scan_file, plan, "7.0.107.76", oracle)
    return source, stage, plan, headers


def test_uop_headers_independent_decode_and_batch_recovery(tmp_path, oracle):
    source, stage, plan, headers = prepare(tmp_path, oracle)
    report = json.loads((plan / "verification/report.json").read_text())
    assert report["outcome"] == "pass" and report["preserved_uop_headers"] == 3
    original = replacement.stage_hashes(source)
    replacement.apply(plan / "plan.json", oracle)
    metadata = {}; records, _ = parity.records(stage, "uop", metadata)
    assert {ident: value[0] for ident, value in metadata.items()} == headers
    assert records[0x4003] == art()
    assert records[0x4001][:4] == art()[:4]
    assert replacement.stage_hashes(source) == original
    replacement.recover(stage)
    assert not stage.exists() and replacement.stage_hashes(source) == original


@pytest.mark.parametrize("damage", ["target-header", "untouched-header", "untouched-checksum", "header-range"])
def test_uop_header_or_metadata_damage_is_detected(tmp_path, oracle, damage):
    source, stage, plan, headers = prepare(tmp_path, oracle)
    path = plan / "candidate/artLegacyMUL.uop"
    data = bytearray(path.read_bytes())
    block = struct.unpack_from("<q", data, 12)[0]
    at = block + 12 + (0 if damage == "target-header" else 2) * 34
    offset = struct.unpack_from("<q", data, at)[0]
    if damage == "untouched-checksum":
        data[at + 28] ^= 1
    elif damage == "header-range":
        struct.pack_into("<i", data, at + 8, len(data))
    else:
        data[offset] ^= 1
    path.write_bytes(data)
    report = parity.verify(plan / "candidate", plan / "baseline", plan / "expected.json", tmp_path / "damaged-report", oracle)
    assert report["outcome"] != "pass"
    if damage != "header-range":
        assert any(row["kind"] in ("uop_entry_header", "untouched_uop_metadata") for row in report["mismatches"])
    assert not stage.exists()


def test_direct_source_header_decode(tmp_path, oracle):
    source, images, stage, headers = fixture(tmp_path)
    import shutil
    shutil.copytree(source, stage)
    raw = art((0x001f,) * 6)
    manifest = tmp_path / "expected.json"
    manifest.write_text(json.dumps({"schema": 1, "packaging": "uop", "client_version": "7.0.107.76",
        "records": [{"id": 1, "width": 3, "height": 2, "rgba": [0xffff0000] * 6,
            "raw_sha256": hashlib.sha256(raw).hexdigest()}]}))
    report = parity.verify(stage, source, manifest, tmp_path / "direct-report", oracle)
    assert report["outcome"] == "pass" and report["preserved_uop_headers"] == 3


def test_hardlinked_read_only_source_is_copied_not_modified(tmp_path, oracle):
    import os
    source, images, stage, headers = fixture(tmp_path)
    archive = source / "artLegacyMUL.uop"
    backup = tmp_path / "backup.uop"; os.link(archive, backup)
    before = parity.digest(backup)
    preview = tmp_path / "scan.json"; preview.write_text(json.dumps(batch.scan(images, source, stage)))
    plan = tmp_path / "plan"
    batch.prepare(preview, plan, "7.0.107.76", oracle)
    assert (plan / "candidate/artLegacyMUL.uop").stat().st_nlink == 1
    assert (plan / "baseline/artLegacyMUL.uop").stat().st_nlink == 1
    replacement.apply(plan / "plan.json", oracle)
    assert parity.digest(archive) == before == parity.digest(backup)
    assert archive.stat().st_nlink == 2


def test_hardlinked_stage_is_still_refused(tmp_path, oracle):
    import os
    source, stage, plan, headers = prepare(tmp_path, oracle)
    replacement.apply(plan / "plan.json", oracle)
    os.link(stage / "artLegacyMUL.uop", tmp_path / "stage-alias.uop")
    with pytest.raises(ValueError, match="Hardlinked stage"):
        batch.scan(tmp_path / "images", source, stage)
