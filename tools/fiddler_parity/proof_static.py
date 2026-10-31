"""Synthetic integration proofs: expected pixels are literals, never GUO decode."""
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import run as parity
import replacement
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer


def art(colors=(0x7c00, 0, 0x001f, 0x0421, 0x03e0, 0x7fff)):
    # Two full three-pixel runs, independent row offsets, nonzero unknown header.
    return (struct.pack("<IhhHH", 0x12345678, 3, 2, 0, 7)
        + struct.pack("<HH3HHH", 0, 3, *colors[:3], 0, 0)
        + struct.pack("<HH3HHH", 0, 3, *colors[3:], 0, 0) + b"preserved-padding")


def make_uop(path, entries, headers=None):
    data = bytearray(struct.pack("<IIIqIi", writer.UOP_MAGIC, 5, 0xFD23EC43, 0, 100, len(entries)))
    table = []
    for ident, raw in entries.items():
        offset = len(data)
        prefix = (headers or {}).get(ident, b"")
        data += prefix
        data += raw
        table.append(struct.pack("<qiiiQIh", offset, len(prefix), len(raw), len(raw),
            parity.uop_hash(writer.ART_PATTERN.format(ident)), 0, 0))
    block = len(data)
    data += struct.pack("<iq", len(table), 0) + b"".join(table)
    struct.pack_into("<q", data, 12, block)
    path.write_bytes(data)


def fixture(root, packaging, *, write=True):
    source = root / "source"
    source.mkdir(parents=True)
    original = art((0x001f,) * 6)
    if packaging == "uop":
        make_uop(source / "artLegacyMUL.uop", {0x4001: original})
    else:
        idx = bytearray(struct.pack("<iii", -1, -1, 0) * (0x4000 + 32))
        struct.pack_into("<iii", idx, 0x4001 * 12, 0, len(original), 0x1234)
        (source / "artidx.mul").write_bytes(idx)
        (source / "art.mul").write_bytes(original)
    stage = writer.Stage(root / "stage", source)
    if write:
        writer.write_records(stage, [AssetRecord("static", 2, art())])
    else:
        for path in source.iterdir():
            shutil.copyfile(path, stage.root / path.name)
    spec = {"schema": 1, "packaging": packaging, "client_version": "7.0.107.76", "records": [{
        "id": 2 if write else 1, "width": 3, "height": 2,
        "rgba": [0xff0000ff, 0, 0xffff0000, 0xff080808, 0xff00ff00, 0xffffffff] if write else [0xffff0000] * 6,
        "raw_sha256": hashlib.sha256(art() if write else original).hexdigest()}]}
    manifest = root / "expected.json"
    manifest.write_text(json.dumps(spec), encoding="utf-8")
    return source, stage.root, manifest


@pytest.fixture(scope="session")
def oracle():
    path = parity.default_oracle()
    # Never turn missing dependencies into skipped certification.
    parity.doctor(path)
    return path


@pytest.mark.parametrize("packaging", ["mul", "uop"])
@pytest.mark.parametrize("write", [False, True], ids=["direct-decode", "existing-writer"])
def test_independent_decode(tmp_path, oracle, packaging, write):
    source, stage, manifest = fixture(tmp_path, packaging, write=write)
    report = parity.verify(stage, source, manifest, tmp_path / "report", oracle)
    assert report["outcome"] == "pass", report
    assert report["inputs_unchanged"]
    assert report["oracle"]["identity"]["OracleCommit"]


@pytest.mark.parametrize("packaging", ["mul", "uop"])
@pytest.mark.parametrize("damage", ["pixel", "id", "offset", "truncate", "untouched", "header", "row-offset"])
def test_corruption_fails(tmp_path, oracle, packaging, damage):
    source, stage, manifest = fixture(tmp_path, packaging)
    if damage == "id":
        spec = json.loads(manifest.read_text())
        spec["records"][0]["id"] = 3
        manifest.write_text(json.dumps(spec))
    else:
        path = stage / ("art.mul" if packaging == "mul" else "artLegacyMUL.uop")
        data = bytearray(path.read_bytes())
        if damage == "truncate":
            del data[-20:]
        elif damage == "offset":
            if packaging == "mul":
                path = stage / "artidx.mul"
                data = bytearray(path.read_bytes())
                struct.pack_into("<i", data, 0x4002 * 12, 0x7fffffff)
            else:
                struct.pack_into("<q", data, 12, len(data) + 100)
        else:
            needle = art((0x001f,) * 6) if damage == "untouched" else art()
            at = data.index(needle)
            at += {"pixel": 16, "untouched": 16, "header": 0, "row-offset": 8}[damage]
            data[at] ^= 0x40 if damage == "row-offset" else 1
        path.write_bytes(data)
    report = parity.verify(stage, source, manifest, tmp_path / "report", oracle)
    assert report["outcome"] != "pass", report
    assert report["inputs_unchanged"]


def test_missing_oracle_fails(tmp_path):
    source, stage, manifest = fixture(tmp_path, "mul")
    report = parity.verify(stage, source, manifest, tmp_path / "report", tmp_path / "absent.dll")
    assert report["outcome"] == "error" and "doctor" in report["error"]


def test_report_overlap_rejected(tmp_path, oracle):
    source, stage, manifest = fixture(tmp_path, "mul")
    with pytest.raises(ValueError, match="overlap"):
        parity.verify(stage, source, manifest, stage / "report", oracle)


def test_manifest_limits(tmp_path, oracle):
    source, stage, manifest = fixture(tmp_path, "mul")
    spec = json.loads(manifest.read_text())
    spec["records"] *= 257
    manifest.write_text(json.dumps(spec))
    with pytest.raises(ValueError, match="256"):
        parity.verify(stage, source, manifest, tmp_path / "report", oracle)


def test_unsupported_compression_fails(tmp_path, oracle):
    source, stage, manifest = fixture(tmp_path, "uop")
    path = stage / "artLegacyMUL.uop"
    data = bytearray(path.read_bytes())
    block = struct.unpack_from("<q", data, 12)[0]
    struct.pack_into("<h", data, block + 12 + 32, 1)
    path.write_bytes(data)
    report = parity.verify(stage, source, manifest, tmp_path / "report", oracle)
    assert report["outcome"] == "unsupported" and report["unsupported"]


@pytest.mark.parametrize("packaging", ["mul", "uop"])
def test_replace_apply_recover(tmp_path, oracle, packaging):
    from PIL import Image
    source, _, _ = fixture(tmp_path / "fixture", packaging)
    image = tmp_path / "replacement.png"
    with Image.new("RGBA", (3, 2), (35, 80, 110, 255)) as img:
        img.putpixel((1, 0), (0, 0, 0, 0))
        img.putpixel((2, 1), (0, 0, 0, 255))
        img.save(image)
    stage, plan = tmp_path / "output", tmp_path / "preview"
    before = replacement.stage_hashes(source)
    prepared = replacement.prepare(source, stage, image, 1, plan, "7.0.107.76", oracle)
    assert prepared["state"] == "prepared" and not stage.exists()
    assert replacement.stage_hashes(source) == before
    replacement.apply(plan / "plan.json", oracle)
    first_stage = replacement.stage_hashes(stage)
    assert first_stage and replacement.stage_hashes(source) == before
    # A second edit retains and can restore the exact prior stage.
    with Image.new("RGBA", (2, 1), (255, 0, 255, 255)) as img:
        img.save(image)
    second = tmp_path / "second-preview"
    replacement.prepare(source, stage, image, 1, second, "7.0.107.76", oracle)
    replacement.apply(second / "plan.json", oracle)
    assert replacement.stage_hashes(stage) != first_stage
    replacement.recover(stage)
    assert replacement.stage_hashes(stage) == first_stage
    assert replacement.stage_hashes(source) == before


def test_stale_candidate_rejected(tmp_path, oracle):
    from PIL import Image
    source, _, _ = fixture(tmp_path / "fixture", "mul")
    image = tmp_path / "image.png"
    with Image.new("RGBA", (1, 1), (255, 0, 0, 255)) as img:
        img.save(image)
    stage, plan = tmp_path / "output", tmp_path / "preview"
    replacement.prepare(source, stage, image, 1, plan, "7.0.107.76", oracle)
    with (plan / "candidate" / "art.mul").open("ab") as stream:
        stream.write(b"changed")
    with pytest.raises(ValueError, match="changed"):
        replacement.apply(plan / "plan.json", oracle)
    assert not stage.exists()


def test_missing_writer_slot_rejected(tmp_path, oracle):
    from PIL import Image
    source, _, _ = fixture(tmp_path / "fixture", "mul")
    image = tmp_path / "image.png"
    with Image.new("RGBA", (1, 1), (255, 0, 0, 255)) as img:
        img.save(image)
    with pytest.raises(ValueError, match="occupied"):
        replacement.prepare(source, tmp_path / "output", image, 3, tmp_path / "preview", "7.0.107.76", oracle)
    assert not (tmp_path / "output").exists()


def prepared_case(root, oracle):
    from PIL import Image
    source, _, _ = fixture(root / "fixture", "mul")
    image = root / "image.png"
    with Image.new("RGBA", (2, 2), (255, 0, 0, 255)) as img:
        img.save(image)
    stage, plan = root / "output", root / "preview"
    replacement.prepare(source, stage, image, 1, plan, "7.0.107.76", oracle)
    return source, image, stage, plan


@pytest.mark.parametrize("target", ["source", "image"])
def test_changed_input_rejected(tmp_path, oracle, target):
    source, image, stage, plan = prepared_case(tmp_path, oracle)
    path = source / "art.mul" if target == "source" else image
    with path.open("ab") as stream:
        stream.write(b"changed")
    with pytest.raises(ValueError, match="changed"):
        replacement.apply(plan / "plan.json", oracle)
    assert not stage.exists()


def test_failed_promotion_restores_prior_stage(tmp_path, oracle, monkeypatch):
    source, image, stage, plan = prepared_case(tmp_path, oracle)
    replacement.apply(plan / "plan.json", oracle)
    before = replacement.stage_hashes(stage)
    second = tmp_path / "second-preview"
    replacement.prepare(source, stage, image, 1, second, "7.0.107.76", oracle)
    rename = Path.rename
    def interrupted(path, target):
        if path == second / "candidate":
            raise OSError("synthetic promotion failure")
        return rename(path, target)
    monkeypatch.setattr(Path, "rename", interrupted)
    with pytest.raises(OSError, match="synthetic"):
        replacement.apply(second / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before
    journal = json.loads(stage.with_name(stage.name + ".fiddler-journal.json").read_text())
    assert journal["state"] == "recovered"


def test_first_stage_failure_restores_absence(tmp_path, oracle, monkeypatch):
    _, _, stage, plan = prepared_case(tmp_path, oracle)
    def failed_save(instance):
        raise OSError("synthetic metadata-save failure")
    monkeypatch.setattr(writer.Stage, "save", failed_save)
    with pytest.raises(OSError, match="synthetic"):
        replacement.apply(plan / "plan.json", oracle)
    assert not stage.exists()
    assert list(tmp_path.glob("output.failed-*"))


def test_interrupted_promotion_can_be_recovered(tmp_path, oracle):
    source, image, stage, plan = prepared_case(tmp_path, oracle)
    replacement.apply(plan / "plan.json", oracle)
    before = replacement.stage_hashes(stage)
    backup = stage.with_name(stage.name + ".previous-interrupted")
    stage.rename(backup)
    journal = stage.with_name(stage.name + ".fiddler-journal.json")
    replacement.json_write(journal, {"schema": 1, "state": "promoting", "stage": str(stage),
        "backup": str(backup), "prior_exists": True})
    replacement.recover(stage)
    assert replacement.stage_hashes(stage) == before


def test_stage_lock_rejects_second_writer(tmp_path):
    stage = tmp_path / "stage"
    with replacement.stage_lock(stage):
        with pytest.raises(OSError):
            with replacement.stage_lock(stage):
                pytest.fail("second writer acquired the stage lock")


def test_recovery_cannot_move_configured_install(tmp_path, monkeypatch):
    from types import SimpleNamespace
    stage = tmp_path / "output"
    stage.mkdir()
    (stage / "keep.txt").write_text("current")
    protected = stage.with_name(stage.name + ".previous-protected-install")
    protected.mkdir()
    (protected / "art.mul").write_bytes(b"protected input")
    config = SimpleNamespace(client_data=protected, client_data_env="", client_data_setting="")
    monkeypatch.setattr(parity, "load_config", lambda: config)
    replacement.json_write(stage.with_name(stage.name + ".fiddler-journal.json"),
        {"schema": 1, "state": "complete", "stage": str(stage), "backup": str(protected), "prior_exists": True})
    with pytest.raises(ValueError, match="read-only"):
        replacement.recover(stage)
    assert (protected / "art.mul").read_bytes() == b"protected input"
    assert (stage / "keep.txt").read_text() == "current"


def test_output_cannot_use_install_alias(tmp_path, oracle):
    import os
    source, _, _ = fixture(tmp_path / "fixture", "mul")
    alias = tmp_path / "install-alias"
    try:
        os.symlink(source, alias, target_is_directory=True)
    except OSError:
        try:
            import _winapi
            _winapi.CreateJunction(str(source), str(alias))
        except (ImportError, OSError):
            pytest.skip("Directory links unavailable; alias proof not certified")
    with pytest.raises(ValueError, match="overlap"):
        parity.verify(tmp_path / "fixture" / "stage", source, tmp_path / "fixture" / "expected.json", alias / "report", oracle)
    assert not (source / "report").exists()


def test_static_apply_rejects_stage_redirected_after_review(tmp_path, oracle):
    import os
    source, image, stage, plan = prepared_case(tmp_path, oracle)
    replacement.apply(plan / "plan.json", oracle)
    second = tmp_path / "second-preview"
    replacement.prepare(source, stage, image, 1, second, "7.0.107.76", oracle)
    redirected = tmp_path / "different-destination"
    stage.rename(redirected)
    before = replacement.stage_hashes(redirected)
    try:
        os.symlink(redirected, stage, target_is_directory=True)
    except OSError:
        import _winapi
        _winapi.CreateJunction(str(redirected), str(stage))
    with pytest.raises(ValueError, match="[Ll]ink|[Aa]lias"):
        replacement.apply(second / "plan.json", oracle)
    assert replacement.stage_hashes(redirected) == before
    assert (second / "candidate").is_dir()
