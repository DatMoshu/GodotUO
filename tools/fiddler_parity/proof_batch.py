"""Batch replacement proofs with synthetic art and the independent upstream oracle."""
import json
from pathlib import Path
import shutil
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import batch
import replacement
import run as parity
from proof_static import art, make_uop, oracle


def dataset(root, packaging):
    source = root / "source"
    source.mkdir()
    records = {0x4001: art((0x001f,) * 6), 0x4002: art((0x03e0,) * 6), 0x4003: art()}
    if packaging == "uop":
        make_uop(source / "artLegacyMUL.uop", records)
    else:
        idx = bytearray(struct.pack("<iii", -1, -1, 0) * 0x4010)
        raw = bytearray()
        for ident, record in records.items():
            struct.pack_into("<iii", idx, ident * 12, len(raw), len(record), 0x4512)
            raw += record
        (source / "art.mul").write_bytes(raw)
        (source / "artidx.mul").write_bytes(idx)
    images = root / "images"
    images.mkdir()
    from PIL import Image
    Image.new("RGBA", (3, 2), (255, 0, 0, 255)).save(images / "static_0x0001.png")
    Image.new("RGBA", (3, 2), (0, 255, 0, 255)).save(images / "2.png")
    return source, images, root / "stage"


@pytest.mark.parametrize("packaging", ["mul", "uop"])
def test_batch_atomic_verified_replacement(tmp_path, oracle, packaging):
    source, images, stage = dataset(tmp_path, packaging)
    original = replacement.stage_hashes(source)
    preview = batch.scan(images, source, stage)
    assert preview["ready"] and preview["count"] == 2 and not stage.exists()
    scan_file = tmp_path / "scan.json"
    scan_file.write_text(json.dumps(preview))
    plan = tmp_path / "plan"
    result = batch.prepare(scan_file, plan, "7.0.107.76", oracle)
    assert result["count"] == 2 and not stage.exists()
    report = json.loads((plan / "verification/report.json").read_text())
    assert report["outcome"] == "pass" and report["checked_ids"] == [2, 1]
    assert report["preserved_records"] == 1
    before, _ = parity.records(source, packaging)
    replacement.apply(plan / "plan.json", oracle)
    after, _ = parity.records(stage, packaging)
    assert after[0x4003] == before[0x4003]
    assert after[0x4001] != before[0x4001]
    assert after[0x4001][:4] == before[0x4001][:4]
    assert after[0x4002][:4] == before[0x4002][:4]
    assert replacement.stage_hashes(source) == original
    replacement.recover(stage)
    assert not stage.exists()


@pytest.mark.parametrize("damage", ["duplicate", "unknown-name", "empty-id", "bad-png", "oversize", "empty-folder"])
def test_batch_preview_reports_errors_without_writes(tmp_path, damage):
    source, images, stage = dataset(tmp_path, "mul")
    if damage == "empty-folder":
        for p in images.iterdir():
            p.unlink()
    elif damage == "duplicate":
        shutil.copyfile(images / "2.png", images / "0x0002.png")
    elif damage == "unknown-name":
        shutil.copyfile(images / "2.png", images / "backpack.png")
    elif damage == "empty-id":
        shutil.copyfile(images / "2.png", images / "0x0004.png")
    elif damage == "bad-png":
        (images / "2.png").write_text("broken")
    else:
        from PIL import Image
        Image.new("RGBA", (1025, 1)).save(images / "2.png")
    before = replacement.stage_hashes(source)
    result = batch.scan(images, source, stage)
    assert not result["ready"] and result["errors"] and not stage.exists()
    assert replacement.stage_hashes(source) == before


@pytest.mark.parametrize("change", ["changed-image", "new-image", "removed-image"])
def test_stale_batch_preview_refused(tmp_path, oracle, change):
    source, images, stage = dataset(tmp_path, "mul")
    scan_file = tmp_path / "scan.json"
    scan_file.write_text(json.dumps(batch.scan(images, source, stage)))
    if change == "new-image":
        shutil.copyfile(images / "2.png", images / "3.png")
    elif change == "removed-image":
        (images / "2.png").unlink()
    else:
        shutil.copyfile(images / "static_0x0001.png", images / "2.png")
    with pytest.raises(ValueError, match="changed since preview"):
        batch.prepare(scan_file, tmp_path / "plan", "7.0.107.76", oracle)
    assert not stage.exists() and not (tmp_path / "plan").exists()


def test_apply_rechecks_every_batch_image(tmp_path, oracle):
    source, images, stage = dataset(tmp_path, "mul")
    scan_file = tmp_path / "scan.json"
    scan_file.write_text(json.dumps(batch.scan(images, source, stage)))
    plan = tmp_path / "plan"
    batch.prepare(scan_file, plan, "7.0.107.76", oracle)
    # Change the first image, rather than only the last image carried by legacy fields.
    shutil.copyfile(images / "static_0x0001.png", images / "2.png")
    with pytest.raises(ValueError, match="image changed"):
        replacement.apply(plan / "plan.json", oracle)
    assert not stage.exists()


@pytest.mark.parametrize("stem,ident", [("26", 26), ("0x001A", 26), ("static_0x001A", 26), ("00026", 26), ("STATIC_0XFFFF", 65535)])
def test_batch_id_contract(stem, ident):
    assert batch.filename_id(stem) == ident


def test_failed_batch_preparation_preserves_existing_stage(tmp_path, oracle, monkeypatch):
    source, images, stage = dataset(tmp_path, "mul")
    scan_file = tmp_path / "scan.json"
    scan_file.write_text(json.dumps(batch.scan(images, source, stage)))
    first = tmp_path / "first"
    batch.prepare(scan_file, first, "7.0.107.76", oracle)
    replacement.apply(first / "plan.json", oracle)
    (stage / "keep.txt").write_text("unrelated stage content")
    before = replacement.stage_hashes(stage)
    scan_file.write_text(json.dumps(batch.scan(images, source, stage)))
    def interrupted(*args):
        raise KeyboardInterrupt("simulated cancellation during candidate verification")
    monkeypatch.setattr(parity, "verify", interrupted)
    with pytest.raises(KeyboardInterrupt):
        batch.prepare(scan_file, tmp_path / "cancelled", "7.0.107.76", oracle)
    assert replacement.stage_hashes(stage) == before


def test_folder_limit_and_pixel_budget_refused(tmp_path):
    source, images, stage = dataset(tmp_path, "mul")
    from PIL import Image
    Image.new("RGBA", (1024, 1024)).save(images / "2.png")
    result = batch.scan(images, source, stage)
    assert not result["ready"] and any("pixels" in e for e in result["errors"])
    for n in range(260):
        (images / f"invalid_{n}.png").write_text("not an image")
    result = batch.scan(images, source, stage)
    assert len(result["rows"]) == 256 and any("256" in e for e in result["errors"])
    assert not stage.exists()


def test_batch_install_and_hardlink_inputs_refused(tmp_path):
    import os
    source, images, stage = dataset(tmp_path, "mul")
    with pytest.raises(ValueError):
        batch.scan(images, source, source / "stage")
    os.link(images / "2.png", images / "0x0002.png")
    result = batch.scan(images, source, stage)
    assert not result["ready"] and any("hardlinked" in e for e in result["errors"])
