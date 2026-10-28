"""Regression for chief review16923: identical input hashes do not imply equal cohorts."""
import json

import pytest

import local_gate
from guo.perf_cohort import effective_cohort, invariant_args, read_arm
from test_local_gate import setup_gate, run_gate, baseline_path
from test_manifest import cohort_report


@pytest.fixture
def cohort_gate(setup_gate, monkeypatch):
    cfg, calls = setup_gate
    original = local_gate.subprocess.run
    state = {"report": cohort_report(), "diagnostics": {"GUO_PERF_MINIMAP": "0"}}

    def run(cmd, **kwargs):
        result = original(cmd, **kwargs)
        label = cmd[cmd.index("--label") + 1]
        path = cfg.build / "perf" / f"perf_{label}.json"
        report = state["report"]
        path.write_text(json.dumps(report))
        path.with_suffix(".manifest.json").write_text(json.dumps({
            "build": {"inputs": {"source": {"head": "synthetic-export-head"}}},
            "inputs": {"configuration": {"profile": "same"}, "fixture": {"route": "same"}},
            "requested": {"diagnostics": state["diagnostics"]}, "cohort": effective_cohort(report)}))
        return result

    monkeypatch.setattr(local_gate.subprocess, "run", run)
    return cfg, calls, state


ARGS = ["--fixture", "test-world", "--manifest-inputs", "private-inputs.json"]


@pytest.mark.parametrize("field,value", [("zoom", 2), ("warmup_seconds", 2), ("measure_seconds", 31),
    ("walk_seconds", 13), ("walk_origin", "bank"), ("walk_route_sha256", "a" * 64),
    ("frame_target", 720), ("runtime_attribution", True), ("profile", "different")])
def test_effective_cohort_drift_rejects_and_preserves_baseline(cohort_gate, field, value):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"][field] = value
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("field,value", [("minimap_open", True), ("window", "800x600"),
                                      ("zoom", 2), ("renderer", "gl_compatibility"), ("warmup_seconds", 1)])
def test_per_scene_drift_rejects_with_same_arguments_and_input_hashes(cohort_gate, field, value):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"]["scenes"][2][field] = value
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("field,value", [("interval_counters", True), ("parity", True),
    ("raw_frames", False), ("minimap_requested", True), ("parity_target", "retained-canvas")])
def test_effective_diagnostic_mode_drift_rejects(cohort_gate, field, value):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"]["diagnostics"][field] = value
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


def test_requested_minimap_drift_rejects_even_if_effective_state_matches(cohort_gate):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["diagnostics"]["GUO_PERF_MINIMAP"] = "1"
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("flag", ["retained_canvas", "clear_used_canvas", "reclaim_empty_retained_canvas", "atlas_page_1024", "minimap_texture"])
def test_unexpected_optimization_flag_drift_rejects(cohort_gate, flag):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"]["runtime_identity"]["effective_flags"][flag] = True
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


def test_explicit_arms_allow_declared_optimization_with_different_cli_flags(cohort_gate, tmp_path):
    cfg, _, state = cohort_gate
    arm = tmp_path / "arm.json"
    arm.write_text(json.dumps({"name": "control", "flags": {"gpu_world": False}}))
    args = [*ARGS, "--experiment-arm", str(arm)]
    assert run_gate([*args, "--record-baseline", "--args=--gpu-world=off"]) == 0
    before = baseline_path(cfg).read_bytes()
    arm.write_text(json.dumps({"name": "candidate", "flags": {"gpu_world": True}}))
    state["report"]["runtime_identity"]["effective_flags"]["gpu_world"] = True
    assert run_gate([*args, "--args=--gpu-world=on"]) == 0
    assert "Declared experiment arms" in next(cfg.build.glob("perf/gate/*/*.md")).read_text()
    state["report"]["scenes"][1]["minimap_open"] = True
    assert run_gate([*args, "--args=--gpu-world=on"]) == 2
    assert baseline_path(cfg).read_bytes() == before


def test_arm_expectation_and_undeclared_flags_still_bind(cohort_gate, tmp_path):
    cfg, _, state = cohort_gate
    arm = tmp_path / "arm.json"
    arm.write_text(json.dumps({"name": "control", "flags": {"retained_canvas": False}}))
    args = [*ARGS, "--experiment-arm", str(arm)]
    assert run_gate([*args, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    arm.write_text(json.dumps({"name": "candidate", "flags": {"retained_canvas": True}}))
    assert run_gate(args) == 2  # request was ignored by runtime
    state["report"]["runtime_identity"]["effective_flags"].update(retained_canvas=True, gpu_world=True)
    assert run_gate(args) == 2  # second flag was not declared
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("missing", ["diagnostics", "frame_target", "walk_route_sha256", "runtime_identity"])
def test_missing_effective_state_cannot_form_a_cohort(missing):
    report = cohort_report()
    del report[missing]
    with pytest.raises(ValueError, match="cohort"):
        effective_cohort(report)


def test_arm_cannot_hide_nonoptimization_cli_or_modes(tmp_path):
    arm = {"name": "candidate", "flags": {"gpu_world": True}}
    assert invariant_args("--gpu-world --perf-parity --perf-profile plain", arm) == "--perf-parity --perf-profile plain"
    path = tmp_path / "arm.json"
    path.write_text(json.dumps({"name": "bad", "flags": {"minimap_open": True}}))
    with pytest.raises(ValueError, match="known boolean"):
        read_arm(path)


def test_invalid_replacement_preserves_baseline(cohort_gate, tmp_path):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"]["runtime_identity"]["renderer"] = "gl_compatibility"
    assert run_gate([*ARGS, "--replace-baseline"]) == 2
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("flags,argument", [
    ({"shared_cover": True, "shared_cover_shader": True}, "--shared-cover"),
    ({"multimesh_sprites": True}, "--multimesh-sprites=on"),
])
def test_renderer_arms_allow_only_declared_changes(cohort_gate, tmp_path, flags, argument):
    cfg, _, state = cohort_gate
    arm = tmp_path / "arm.json"
    arm.write_text(json.dumps({"name": "off", "flags": dict.fromkeys(flags, False)}))
    args = [*ARGS, "--experiment-arm", str(arm)]
    assert run_gate([*args, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    arm.write_text(json.dumps({"name": "on", "flags": flags}))
    state["report"]["runtime_identity"]["effective_flags"].update(flags)
    assert run_gate([*args, "--args=" + argument]) == 0
    state["report"]["runtime_identity"]["effective_flags"]["shared_cover_reference"] = True
    assert run_gate([*args, "--args=" + argument]) == 2
    assert baseline_path(cfg).read_bytes() == before


def test_multimesh_threshold_drift_preserves_baseline(cohort_gate):
    cfg, _, state = cohort_gate
    assert run_gate([*ARGS, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    state["report"]["runtime_identity"]["parameters"]["multimesh_min_run"] = 16
    assert run_gate(ARGS) == 2
    assert baseline_path(cfg).read_bytes() == before


@pytest.mark.parametrize("value", [None, {}, {"multimesh_min_run": True}, {"multimesh_min_run": 8.0}])
def test_missing_or_malformed_renderer_parameters_rejected(value):
    report = cohort_report()
    report["runtime_identity"]["parameters"] = value
    with pytest.raises(ValueError):
        effective_cohort(report)


def test_reclamation_explicit_arm_and_missing_identity(cohort_gate, tmp_path):
    cfg, _, state = cohort_gate
    arm = tmp_path / "arm.json"
    flag = "reclaim_empty_retained_canvas"
    arm.write_text(json.dumps({"name": "control", "flags": {flag: False}}))
    args = [*ARGS, "--experiment-arm", str(arm)]
    state["report"]["runtime_identity"]["effective_flags"]["retained_canvas"] = True
    assert run_gate([*args, "--record-baseline"]) == 0
    before = baseline_path(cfg).read_bytes()
    arm.write_text(json.dumps({"name": "candidate", "flags": {flag: True}}))
    state["report"]["runtime_identity"]["effective_flags"][flag] = True
    assert run_gate(args) == 0
    assert baseline_path(cfg).read_bytes() == before
    del state["report"]["runtime_identity"]["effective_flags"][flag]
    with pytest.raises(ValueError, match="Incomplete optimization flags"):
        effective_cohort(state["report"])


@pytest.mark.parametrize("flag", ["atlas_page_1024", "minimap_texture"])
def test_missing_atlas_flags_require_fresh_baseline(flag):
    report = cohort_report()
    del report["runtime_identity"]["effective_flags"][flag]
    with pytest.raises(ValueError, match="Incomplete optimization flags"):
        effective_cohort(report)
