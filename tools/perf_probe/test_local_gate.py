import json
from types import SimpleNamespace

import pytest

import local_gate
from test_gate import report


@pytest.fixture
def setup_gate(tmp_path, monkeypatch):
    engine = tmp_path / "engine.exe"
    engine.touch()
    data = tmp_path / "install"
    data.mkdir()
    cfg = SimpleNamespace(root=tmp_path / "checkout", build=tmp_path / "output",
                          godot_console_exe=engine, client_data=data, client_version="test-version",
                          shard_host="localhost", shard_port=2593)
    monkeypatch.setattr(local_gate, "load_config", lambda: cfg)
    calls = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        label = cmd[cmd.index("--label") + 1]
        out = cfg.build / "perf" / f"perf_{label}.json"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(report()), encoding="utf-8")
        return SimpleNamespace(returncode=0)

    monkeypatch.setattr(local_gate.subprocess, "run", run)
    return cfg, calls


def baseline_path(cfg):
    return next(cfg.build.glob("perf/gate/*/default.baseline.json"))


def test_record_then_compare_and_refuse_implicit_overwrite(setup_gate):
    cfg, calls = setup_gate
    args = ["--fixture", "test-world"]
    assert local_gate.main(args) == 2  # no baseline; no engine launch
    assert not calls
    assert local_gate.main([*args, "--record-baseline"]) == 0
    path = baseline_path(cfg)
    before = path.read_bytes()
    assert local_gate.main([*args, "--record-baseline"]) == 2
    assert path.read_bytes() == before
    assert len(calls) == 1
    assert local_gate.main(args) == 0
    assert path.read_bytes() == before
    assert calls[0][calls[0].index("--renderer") + 1] == "forward_plus"


def test_gate_asks_the_probe_for_its_own_scenes_and_a_static_world(setup_gate):
    """PERF-V5/V6: a stock run needs no --args; the gate names its six scenes and the map-only walk."""
    cfg, calls = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline", "--args=--batched-world"]) == 0
    passed = next(a for a in calls[0] if a.startswith("--args="))[len("--args="):].split()
    assert passed[0] == "--perf-static-world"
    assert passed.count("--perf-scene") == 5 and "login" not in passed and "walk" in passed
    assert passed[-1] == "--batched-world"
    doc = json.loads(baseline_path(cfg).read_text())
    assert doc["context"]["gate_flags"] == " ".join(local_gate.GATE_FLAGS)
    assert doc["context"]["args"] == "--batched-world"
    assert set(doc["provenance"]) == {"head", "dirty", "client_dll_sha256"}


def test_dynamic_world_report_cannot_record(setup_gate, monkeypatch):
    cfg, calls = setup_gate

    def run(cmd, **kwargs):
        label = cmd[cmd.index("--label") + 1]
        out = cfg.build / "perf" / f"perf_{label}.json"
        out.parent.mkdir(parents=True, exist_ok=True)
        doc = report()
        doc["static_world"] = False
        out.write_text(json.dumps(doc), encoding="utf-8")
        return SimpleNamespace(returncode=0)

    monkeypatch.setattr(local_gate.subprocess, "run", run)
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 2
    assert not list(cfg.build.glob("perf/gate/*/default.baseline.json"))


@pytest.mark.parametrize("change", [["--zoom", "2.5"], ["--renderer", "gl_compatibility"],
                                    ["--size", "1920,1080"], ["--args=--batched-world"],
                                    ["--port", "2594"], ["--fixture", "changed-world"]])
def test_context_mismatch_stops_before_probe(setup_gate, change):
    _, calls = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 0
    assert local_gate.main(["--fixture", "test-world", *change]) == 2
    assert len(calls) == 1


def test_failed_probe_cannot_replace_baseline(setup_gate, monkeypatch):
    cfg, _ = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 0
    path = baseline_path(cfg)
    before = path.read_bytes()
    monkeypatch.setattr(local_gate.subprocess, "run", lambda *a, **kw: SimpleNamespace(returncode=1))
    assert local_gate.main(["--fixture", "new-world", "--replace-baseline"]) == 2
    assert path.read_bytes() == before


def test_count_failure_returns_one_and_preserves_baseline(setup_gate, monkeypatch):
    cfg, _ = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 0
    path = baseline_path(cfg)
    before = path.read_bytes()
    original = local_gate.subprocess.run

    def regression(cmd, **kwargs):
        result = original(cmd, **kwargs)
        measured_path = cfg.build / "perf" / f"perf_{cmd[cmd.index('--label') + 1]}.json"
        measured = json.loads(measured_path.read_text())
        measured["scenes"][1]["draw_calls"] = 106.0
        measured_path.write_text(json.dumps(measured))
        return result

    monkeypatch.setattr(local_gate.subprocess, "run", regression)
    assert local_gate.main(["--fixture", "test-world"]) == 1
    assert path.read_bytes() == before


@pytest.mark.parametrize("change", ["machine", "version"])
def test_changed_machine_or_client_version_cannot_compare(setup_gate, monkeypatch, change):
    cfg, calls = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 0
    if change == "machine":
        monkeypatch.setattr(local_gate.platform, "node", lambda: "another-machine")
    else:
        cfg.client_version = "new-client-version"
    assert local_gate.main(["--fixture", "test-world"]) == 2
    assert len(calls) == 1


def test_explicit_replacement_can_change_context(setup_gate):
    cfg, _ = setup_gate
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 0
    assert local_gate.main(["--fixture", "new-world", "--replace-baseline"]) == 0
    assert json.loads(baseline_path(cfg).read_text())["context"]["fixture"] == "new-world"


def test_doctor_never_connects_or_launches(setup_gate):
    _, calls = setup_gate
    assert local_gate.main(["--doctor"]) == 0
    assert not calls


def test_output_creation_error_is_plain_failure(setup_gate, capsys):
    cfg, calls = setup_gate
    cfg.build.touch()  # a file blocks creation of the output directory
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 2
    assert not calls
    assert "perf_gate --doctor" in capsys.readouterr().out


def test_unreadable_configuration_is_plain_failure(monkeypatch, capsys):
    def denied():
        raise PermissionError("denied")
    monkeypatch.setattr(local_gate, "load_config", denied)
    assert local_gate.main(["--doctor"]) == 2
    assert "Cannot read local configuration" in capsys.readouterr().out


def test_baseline_cannot_be_written_in_checkout(setup_gate):
    cfg, calls = setup_gate
    cfg.build = cfg.root / "build"
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 2
    assert not calls
    assert not cfg.build.exists()


def test_wrong_actual_window_cannot_record(setup_gate, monkeypatch):
    cfg, calls = setup_gate
    original = local_gate.subprocess.run

    def wrong_window(cmd, **kwargs):
        result = original(cmd, **kwargs)
        path = cfg.build / "perf" / f"perf_{cmd[cmd.index('--label') + 1]}.json"
        doc = json.loads(path.read_text())
        doc["window"] = "640x480"
        path.write_text(json.dumps(doc))
        return result

    monkeypatch.setattr(local_gate.subprocess, "run", wrong_window)
    assert local_gate.main(["--fixture", "test-world", "--record-baseline"]) == 2
    assert not list(cfg.build.glob("perf/gate/*/*.baseline.json"))


@pytest.mark.parametrize("args", [["--fixture", "../escape"], ["--name", "../escape"],
                                  ["--fixture", "test", "--zoom", "nan"], ["--fixture", "test", "--port", "0"],
                                  ["--fixture", "test", "--size", "-1,720"], []])
def test_invalid_arguments_rejected_before_config(args, monkeypatch):
    monkeypatch.setattr(local_gate, "load_config", lambda: pytest.fail("must not load config"))
    with pytest.raises(SystemExit) as exc:
        local_gate.main(args)
    assert exc.value.code == 2
