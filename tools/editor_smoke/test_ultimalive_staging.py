"""Producer lifecycle regressions; fake exporter, no game install or Godot."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location("ultimalive_stage", Path(__file__).with_name("ultimalive_staging.py"))
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)


@pytest.fixture
def cycle(tmp_path, monkeypatch):
    out = tmp_path / "evidence"
    project = out / "world_project"
    block = project / "blocks/0/187_203.json"
    block.parent.mkdir(parents=True)
    block.write_text(json.dumps({"statics": []}))
    (project / "project.json").write_text('{"base":{"fingerprint":"fixture"}}')
    original = block.read_bytes()
    client = tmp_path / "install"
    client.mkdir()
    import guo
    monkeypatch.setattr(guo, "load_config", lambda: SimpleNamespace(client_data=client, root=tmp_path / "repository"))
    monkeypatch.setattr(stage, "peak_bytes", lambda *_: 1024)
    monkeypatch.setattr(stage.shutil, "disk_usage", lambda _: SimpleNamespace(free=stage.RESERVE_BYTES + 2048))
    root = tmp_path / "_disposable/guo/editor-smoke"
    calls = []

    def tool(command, *args):
        def value(key):
            return Path(args[args.index(key) + 1])
        export = value("--out")
        source = value("--project")
        current_stage = export.parent
        manifest = json.loads((current_stage / ".org-cache.json").read_text())
        assert not manifest["completed"] and manifest["pins"]
        calls.append(command)
        if command == "export":
            export.mkdir()
            data = (source / "blocks/0/187_203.json").read_bytes()
            refs = {}
            for name in ("map0.mul", "staidx0.mul", "statics0.mul"):
                (export / name).write_bytes(data)
                refs[name] = {"bytes": len(data), "sha1": hashlib.sha1(data).hexdigest()}
            (export / "export.json").write_text(json.dumps({"facets": {"0": {"files": refs}}}))
            return 0, "WARNING lacks 1 of this export's 1 block(s) --clear-ultimalive" if export.name == "ul_export2" else "exported"
        if command == "ultimalive":
            if "--clear-ultimalive" not in args:
                return 0, "already has this export"
            shard = value("--ultimalive-root") / "GUO-Smoke"
            for file in shard.iterdir():
                file.unlink()
            return 0, "removed"
        assert command == "verify" and export.name == "retrieval"
        assert all((current_stage / name / "map0.mul").exists() for name in ("ul_export1", "ul_export2", "retrieval"))
        return 0, "verified"

    return SimpleNamespace(out=out, project=project, block=block, original=original, root=root, tool=tool, calls=calls)


def receipt(cycle):
    return json.loads(next((cycle.out / "ultimalive").glob("cycle-*/receipt.json")).read_text())


def test_cycle_retrieves_before_releasing_and_preserves_original_overlay(cycle):
    assert stage.run_checks(cycle.out, cycle.tool, cycle.root) == []
    result = receipt(cycle)
    assert result["completed"] and result["released"] and all(result["checks"].values())
    assert cycle.block.read_bytes() == cycle.original
    assert cycle.calls == ["export", "ultimalive", "export", "ultimalive", "export", "verify"]
    assert list(Path(result["stage"]).rglob("*.mul")) == []
    manifest = json.loads((Path(result["stage"]) / ".org-cache.json").read_text())
    assert manifest["completed"] and manifest["pins"] == []
    evidence = Path(result["evidence"])
    assert (evidence / "first-export.json").is_file() and (evidence / "second-export.json").is_file()
    assert json.loads((evidence / "inputs-first/blocks/0/187_203.json").read_text())["statics"] == []
    assert len(json.loads((evidence / "inputs-second/blocks/0/187_203.json").read_text())["statics"]) == 1


def test_low_capacity_starts_no_export_or_payload(cycle, monkeypatch):
    monkeypatch.setattr(stage.shutil, "disk_usage", lambda _: SimpleNamespace(free=stage.RESERVE_BYTES))
    assert "capacity refused" in stage.run_checks(cycle.out, cycle.tool, cycle.root)[0]
    assert cycle.calls == [] and not cycle.root.exists()


def test_failed_consumer_retains_pinned_stage(cycle):
    def fail(command, *args):
        return (1, "forced verify failure") if command == "verify" else cycle.tool(command, *args)
    assert "verify failed" in stage.run_checks(cycle.out, fail, cycle.root)[0]
    result = receipt(cycle)
    assert not result["released"]
    manifest = json.loads((Path(result["stage"]) / ".org-cache.json").read_text())
    assert not manifest["completed"] and manifest["pins"]
    assert (Path(result["stage"]) / "ul_export2/map0.mul").is_file()


def test_replay_drift_prevents_release(cycle):
    def drift(command, *args):
        result = cycle.tool(command, *args)
        if command == "export":
            export = Path(args[args.index("--out") + 1])
            if export.name == "retrieval":
                (export / "map0.mul").write_bytes(b"drift")
        return result
    assert "readback failed" in stage.run_checks(cycle.out, drift, cycle.root)[0]
    assert not receipt(cycle)["released"]


def test_unexpected_source_is_never_released(tmp_path):
    root = tmp_path / "stage"
    root.mkdir()
    (root / "map0.mul").write_bytes(b"map")
    (root / "source.cs").write_text("preserve")
    with pytest.raises(ValueError, match="unexpected"):
        stage.release(root)
    assert (root / "map0.mul").exists() and (root / "source.cs").exists()


def test_linked_ancestor_is_refused(tmp_path):
    target = tmp_path / "real"
    target.mkdir()
    link = tmp_path / "alias"
    try:
        link.symlink_to(target, target_is_directory=True)
    except OSError as error:
        pytest.skip(f"symlink permission unavailable: {error}")
    with pytest.raises(ValueError, match="link/reparse"):
        stage.create_stage(link / "_disposable/guo", 1, ())


def test_hardlinked_payload_is_not_released(tmp_path):
    root = tmp_path / "stage"
    root.mkdir()
    original = tmp_path / "original.mul"
    original.write_bytes(b"preserve")
    (root / "map0.mul").hardlink_to(original)
    with pytest.raises(ValueError, match="hardlinked"):
        stage.release(root)
    assert original.read_bytes() == b"preserve"


def test_overlapping_evidence_is_refused(tmp_path):
    root = tmp_path / "_disposable/guo"
    with pytest.raises(ValueError, match="separate"):
        stage.create_stage(root, 1, (root / "evidence",))


def test_interrupted_child_keeps_pin(cycle):
    def interrupt(*args):
        raise KeyboardInterrupt
    with pytest.raises(KeyboardInterrupt):
        stage.run_checks(cycle.out, interrupt, cycle.root)
    result = receipt(cycle)
    assert not result["released"]
    assert json.loads((Path(result["stage"]) / ".org-cache.json").read_text())["pins"]
