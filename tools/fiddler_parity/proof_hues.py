"""Independent hue stage integration: literal records, corruptions and recovery."""
import base64
import json
from pathlib import Path
import shutil
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hues
import replacement
import run as parity


def record(seed):
    return struct.pack("<34H", *[(seed * 123 + i * 2017) & 65535 for i in range(32)], 0x8000, 0xffff) + b"raw name\0" + bytes(range(11))


def fixture(root):
    source = root / "source"; source.mkdir()
    blob = b"".join(struct.pack("<I", 0x87654321 + g) + b"".join(record(g * 8 + i) for i in range(8)) for g in range(2))
    (source / "hues.mul").write_bytes(blob)
    change = record(33)
    rows = [{"id": i, "source_record_base64": base64.b64encode(blob[hues.offset(i, len(blob)):hues.offset(i, len(blob)) + 88]).decode(),
        "record_base64": base64.b64encode(change).decode()} for i in (1, 8, 9, 16)]
    edit = root / "edits.json"; edit.write_text(json.dumps({"schema": 1, "domain": "hue", "records": rows}))
    return source, root / "stage", edit, root / "plan", blob


@pytest.fixture(scope="session")
def oracle():
    path = parity.default_oracle(); parity.doctor(path); return path


def prepared(root, oracle):
    source, stage, edit, plan, blob = fixture(root)
    spec = hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    return source, stage, edit, plan, blob, spec


def test_native_group_boundaries_independent_and_recover_absence(tmp_path, oracle):
    source, stage, edit, plan, blob, spec = prepared(tmp_path, oracle)
    report = json.loads((plan / "verification/report.json").read_text())
    assert report["outcome"] == "pass" and report["preserved_headers"] == 2 and report["preserved_records"] == 12
    assert not stage.exists()
    hues.apply(plan / "plan.json", oracle)
    current = (stage / "hues.mul").read_bytes()
    assert current != blob and len(current) == len(blob) and (source / "hues.mul").read_bytes() == blob
    for ident in (1, 8, 9, 16):
        at = hues.offset(ident, len(blob)); assert current[at:at + 88] == record(33)
    assert json.loads((stage / "stage.json").read_text())["files"]["hues.mul"]["source_size"] == len(blob)
    assert str(stage / "hues.mul") in (stage / "files_override.txt").read_text()
    replacement.recover(stage)
    assert not stage.exists() and list(tmp_path.glob("stage.recovered-candidate-*"))


def test_prior_stage_and_unrelated_files_recover_exactly(tmp_path, oracle):
    source, stage, edit, plan, blob = fixture(tmp_path)
    staged = hues.writer.Stage(stage, source); staged.path("hues.mul"); staged.save()
    existing = bytearray((stage / "hues.mul").read_bytes())
    existing[92:180] = record(99)
    (stage / "hues.mul").write_bytes(existing)
    (stage / "notes.txt").write_bytes(b"keep unrelated file")
    prior = replacement.stage_hashes(stage)
    hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle); hues.apply(plan / "plan.json", oracle)
    assert (stage / "notes.txt").read_bytes() == b"keep unrelated file"
    assert (stage / "hues.mul").read_bytes()[92:180] == record(99)
    replacement.recover(stage); assert replacement.stage_hashes(stage) == prior


@pytest.mark.parametrize("damage", ["changed-color", "changed-bit15", "name-padding", "untouched-record", "group-header", "truncate", "append"])
def test_corruption_refused(tmp_path, oracle, damage):
    source, stage, edit, plan, blob, spec = prepared(tmp_path, oracle)
    path = plan / "candidate/hues.mul"; value = bytearray(path.read_bytes())
    if damage == "truncate": value.pop()
    elif damage == "append": value += b"\0"
    else:
        at = {"changed-color": 4, "changed-bit15": 5, "name-padding": 4 + 87, "untouched-record": 4 + 88, "group-header": 708}[damage]
        value[at] ^= 0x80 if damage == "changed-bit15" else 1
    path.write_bytes(value)
    report = hues.verify(plan / "baseline", plan / "candidate", plan / "expected.json", tmp_path / "bad-report", oracle)
    assert report["outcome"] != "pass" and report["inputs_unchanged"]
    with pytest.raises(ValueError): hues.apply(plan / "plan.json", oracle)
    assert not stage.exists()


@pytest.mark.parametrize("change", ["source", "edits", "manifest", "stage"])
def test_stale_inputs_refuse_before_promotion(tmp_path, oracle, change):
    source, stage, edit, plan, blob, spec = prepared(tmp_path, oracle)
    if change == "stage": stage.mkdir(); (stage / "new.txt").write_text("external")
    else:
        path = {"source": source / "hues.mul", "edits": edit, "manifest": plan / "expected.json"}[change]
        path.write_bytes(path.read_bytes() + b" ")
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError): hues.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("invalid", ["zero", "past-end", "duplicate", "short", "foreign-base", "truncated-source", "verdata"])
def test_invalid_edits_never_create_stage(tmp_path, oracle, invalid):
    source, stage, edit, plan, blob = fixture(tmp_path)
    spec = json.loads(edit.read_text()); row = spec["records"][0]
    if invalid == "zero": row["id"] = 0
    elif invalid == "past-end": row["id"] = 17
    elif invalid == "duplicate": spec["records"].append(row)
    elif invalid == "short": row["record_base64"] = "AA=="
    elif invalid == "foreign-base": row["source_record_base64"] = base64.b64encode(bytes(88)).decode()
    elif invalid == "truncated-source": (source / "hues.mul").write_bytes(blob[:-1])
    else: (source / "verdata.mul").write_bytes(b"")
    edit.write_text(json.dumps(spec))
    with pytest.raises(ValueError): hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    assert not stage.exists()


def test_missing_oracle_is_failure(tmp_path, oracle):
    source, stage, edit, plan, blob, spec = prepared(tmp_path, oracle)
    report = hues.verify(plan / "baseline", plan / "candidate", plan / "expected.json", tmp_path / "no-oracle", tmp_path / "missing.dll")
    assert report["outcome"] == "error" and "doctor" in report["error"]


def test_failed_promotion_restores_prior_stage(tmp_path, oracle, monkeypatch):
    source, stage, edit, plan, blob = fixture(tmp_path)
    staged = hues.writer.Stage(stage, source); staged.path("hues.mul"); staged.save(); before = replacement.stage_hashes(stage)
    hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    original = Path.rename
    def fail_candidate(path, target):
        if path == plan / "candidate": raise OSError("simulated interrupted rename")
        return original(path, target)
    monkeypatch.setattr(Path, "rename", fail_candidate)
    with pytest.raises(OSError): hues.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


def test_install_and_nested_paths_refused(tmp_path, oracle):
    source, stage, edit, plan, blob = fixture(tmp_path)
    for bad in (source, source / "stage", source.parent):
        with pytest.raises(ValueError): hues.prepare(source, bad, edit, plan, "7.0.107.76", oracle)
    assert (source / "hues.mul").read_bytes() == blob


def test_valid_stage_path_containing_refused_word(tmp_path, oracle):
    source, stage, edit, plan, blob = fixture(tmp_path)
    stage = tmp_path / "previously-refused-export"
    hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    hues.apply(plan / "plan.json", oracle)
    assert (stage / "hues.mul").exists()


def test_hardlinked_stage_output_refused(tmp_path, oracle):
    import os
    source, stage, edit, plan, blob = fixture(tmp_path)
    staged = hues.writer.Stage(stage, source); staged.path("hues.mul"); staged.save()
    os.link(stage / "hues.mul", tmp_path / "aliased-hues.mul")
    before = (stage / "hues.mul").read_bytes()
    with pytest.raises(ValueError, match="Hardlinked"):
        hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    assert (stage / "hues.mul").read_bytes() == before


def test_output_parent_traversal_refused(tmp_path):
    with pytest.raises(ValueError, match="traversal"):
        hues.output(tmp_path / "sub" / ".." / "stage")


def test_linked_stage_root_refused(tmp_path, oracle):
    source, stage, edit, plan, blob = fixture(tmp_path)
    target = tmp_path / "linked-target"; target.mkdir()
    try:
        stage.symlink_to(target, target_is_directory=True)
    except OSError as error:
        pytest.skip(f"Directory symlink privilege unavailable: {error}")
    with pytest.raises(ValueError, match="Linked"):
        hues.prepare(source, stage, edit, plan, "7.0.107.76", oracle)
    assert list(target.iterdir()) == []
