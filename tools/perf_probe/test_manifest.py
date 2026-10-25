"""Synthetic identities: never launch a game or claim performance evidence."""
import copy
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.perf_manifest import cohort_inputs, export_files, measured_manifest, record_build, verify_build
from guo.perf_manifest import raw_identities
from test_local_gate import setup_gate, run_gate, baseline_path


@pytest.fixture
def export(tmp_path):
    exe = tmp_path / "GUO.exe"
    exe.write_bytes(b"native release")
    data = tmp_path / "data_GUO_windows_x86_64"
    data.mkdir()
    (data / "GUO.dll").write_bytes(b"client")
    (data / "GodotSharp.dll").write_bytes(b"api")
    (data / "dependency.dll").write_bytes(b"dependency")
    exe.with_suffix(".pck").write_bytes(b"resources")
    return exe


def receipt(exe):
    before = {"source": {"head": "a" * 40, "tree": "b" * 40, "dirty": False},
              "engine_sha256": "c" * 64, "sdk": "8.0", "configs": {"project": "d" * 64}}
    return record_build(exe, before, copy.deepcopy(before), ["console", "--export-release", "Windows Desktop", str(exe)])


def runtime(build):
    identity = {field: build["files"][build[role]] for field, role in (
        ("executable_sha256", "executable"), ("client_sha256", "client"), ("godot_api_sha256", "godot_api"))}
    identity.update(client_mvid="client-id", godot_api_mvid="api-id", engine_version="4.7.2", dotnet_runtime=".NET 8", os="test", renderer="forward_plus", effective_flags={"gpu_world": False})
    return dict(runtime_identity=identity, godot_api_mvid="api-id", optimized=True,
                godot_api_jit_optimized=True, engine_debug_build=False, export_template="release")


def test_manifest_binds_export_and_loaded_files(export):
    build = receipt(export)
    assert verify_build(export) == build
    assert build["pack"] == "GUO.pck"
    inputs = {"configuration": {"profile": "hash"}, "fixture": {"route": "hash"}}
    result = measured_manifest(build, runtime(build), inputs, inputs, {"zoom": 2})
    assert result["build"]["files"] == export_files(export)["files"]


@pytest.mark.parametrize("name", ["GUO.dll", "GodotSharp.dll"])
def test_manifest_rejects_missing_assembly(export, name):
    (export.parent / "data_GUO_windows_x86_64" / name).unlink()
    with pytest.raises(ValueError, match="exactly one"):
        export_files(export)


def test_manifest_rejects_ambiguous_client(export):
    extra = export.parent / "data_other"
    extra.mkdir()
    (extra / "GUO.dll").write_bytes(b"stale")
    with pytest.raises(ValueError, match="found 2"):
        export_files(export)


@pytest.mark.parametrize("relative", ["GUO.exe", "GUO.pck", "data_GUO_windows_x86_64/dependency.dll"])
def test_manifest_rejects_changed_build(export, relative):
    receipt(export)
    (export.parent / relative).write_bytes(b"changed")
    with pytest.raises(ValueError, match="changed"):
        verify_build(export)


def test_manifest_rejects_dirty_or_changed_source(export):
    before = {"source": {"dirty": True}}
    with pytest.raises(ValueError, match="clean"):
        record_build(export, before, before, ["--export-release"])
    with pytest.raises(ValueError, match="unchanged"):
        record_build(export, {"source": {"dirty": False}}, before, ["--export-release"])
    with pytest.raises(ValueError, match="explicit Release"):
        record_build(export, {"source": {"dirty": False}}, {"source": {"dirty": False}}, ["--export-debug"])


@pytest.mark.parametrize("field,value", [("optimized", False), ("engine_debug_build", True),
                                        ("godot_api_jit_optimized", False), ("export_template", "debug")])
def test_manifest_rejects_debug_runtime(export, field, value):
    build = receipt(export)
    report = runtime(build)
    report[field] = value
    with pytest.raises(ValueError, match="Release"):
        measured_manifest(build, report, {}, {}, {})


@pytest.mark.parametrize("field", ["executable_sha256", "client_sha256", "godot_api_sha256", "client_mvid", "engine_version"])
def test_manifest_rejects_missing_runtime_identity(export, field):
    build = receipt(export)
    report = runtime(build)
    del report["runtime_identity"][field]
    with pytest.raises(ValueError):
        measured_manifest(build, report, {}, {}, {})


def test_cohort_hashes_only_and_refuses_drift(export):
    config = export.parent / "profile.json"
    config.write_text('"private-value"')
    spec = export.parent / "inputs.json"
    spec.write_text(json.dumps({"configuration": {"profile": "profile.json"}, "fixture": {"route": "GUO.pck"}}))
    before = cohort_inputs(spec)
    assert "private-value" not in json.dumps(before)
    config.write_text('"changed"')
    build = receipt(export)
    with pytest.raises(ValueError, match="changed during"):
        measured_manifest(build, runtime(build), before, cohort_inputs(spec), {})
    spec.write_text('{}')
    with pytest.raises(ValueError, match="nonempty"):
        cohort_inputs(spec)


def test_raw_output_is_after_measurement_and_before_sorting():
    source = (Path(__file__).parents[2] / "godot/GUO/src/Bootstrap/PerfProbe.cs").read_text()
    boundary = source.index("PerfHostLoad.Enter(PerfHostLoad.Phase.Between)", source.index("ms.Add(frameMs)"))
    output = source.index("PerfRawFrames.Write")
    assert boundary < output < source.index("double[] sorted = ms.OrderBy")
    assert 'GetEnvironmentVariable("GUO_PERF_RAW_FRAMES") == "1"' in source[boundary:output]


@pytest.mark.parametrize("change", [{"dropped": 1}, {"storage_grew": True}, {"initial_capacity": 1},
                                   {"frames": 3}, {"frame_ms": [1, -2]}, {"frame_ms": [1, 3]}])
def test_raw_manifest_refuses_loss_or_mismatch(tmp_path, change):
    raw = dict(schema="guo/raw-frames@1", frames=2, dropped=0, storage_grew=False,
               initial_capacity=8, frame_ms=[1, 2])
    path = tmp_path / "frames.json"
    report = {"scenes": [dict(scene="bank", raw_frames="frames.json", frames=2,
                              sample_storage_grew=False, elapsed_seconds=.003)]}
    path.write_text(json.dumps(raw))
    assert raw_identities(report, tmp_path)[0]["frames"] == 2
    raw.update(change)
    path.write_text(json.dumps(raw))
    with pytest.raises(ValueError):
        raw_identities(report, tmp_path)


def test_gate_manifest_records_build_head_and_refuses_fixture_drift(setup_gate, monkeypatch):
    import local_gate
    cfg, calls = setup_gate
    original = local_gate.subprocess.run
    inputs = {"configuration": {"profile": "first"}, "fixture": {"route": "fixed"}}
    def run(cmd, **kwargs):
        result = original(cmd, **kwargs)
        label = cmd[cmd.index("--label") + 1]
        assert "--manifest-inputs" in cmd
        (cfg.build / "perf" / f"perf_{label}.manifest.json").write_text(json.dumps({
            "build": {"inputs": {"source": {"head": "exact-export-head"}}}, "inputs": inputs}))
        return result
    monkeypatch.setattr(local_gate.subprocess, "run", run)
    args = ["--fixture", "test-world", "--manifest-inputs", "private-inputs.json"]
    assert run_gate([*args, "--record-baseline"]) == 0
    path = baseline_path(cfg)
    before = path.read_bytes()
    assert json.loads(before)["provenance"]["head"] == "exact-export-head"
    assert run_gate(args) == 0
    inputs["configuration"]["profile"] = "changed"
    assert run_gate(args) == 2
    assert path.read_bytes() == before
