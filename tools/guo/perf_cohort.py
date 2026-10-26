"""Measurement cohorts and explicitly declared optimization arms; no timing policy."""
from __future__ import annotations

import json
import re
import shlex
from pathlib import Path

FLAGS = frozenset(("batched_world", "merged_cover", "merged_land", "merged_land_ordered",
                   "merged_land_array", "gpu_world", "retained_canvas", "retained_cover_candidates",
                   "shared_cover", "shared_cover_shader", "shared_cover_reference", "multimesh_sprites",
                   "clear_used_canvas", "reclaim_empty_retained_canvas"))
ARG_FLAGS = {
    "--batched-world": {"batched_world"},
    "--merged-cover": {"merged_cover"}, "--merged-cover=off": {"merged_cover"},
    **{arg: {"merged_land", "merged_land_ordered", "merged_land_array"}
       for arg in ("--merged-land", "--merged-land=off", "--merged-land=ordered", "--merged-land=array")},
    **{arg: {"gpu_world"} for arg in ("--gpu-world", "--gpu-world=on", "--gpu-world=off")},
    "--shared-cover": {"shared_cover", "shared_cover_shader"},
    **{arg: {"multimesh_sprites"} for arg in ("--multimesh-sprites", "--multimesh-sprites=on", "--multimesh-sprites=off")},
}
EFFECTIVE = ("window", "zoom", "postfx", "profile", "profile_preset", "static_world", "quiet_fixture",
             "runtime_attribution", "warmup_seconds", "measure_seconds", "walk_seconds", "walk_origin",
             "walk_route_sha256", "sample_window_version", "route_observation_version", "frame_target",
             "diagnostics")
SCENE = ("window", "zoom", "renderer", "minimap_open", "warmup_seconds")
DIAGNOSTICS = ("interval_counters", "parity", "parity_target", "raw_frames", "minimap_requested")


def required(source: dict, fields, label: str) -> dict:
    if not isinstance(source, dict) or any(key not in source or source[key] is None for key in fields):
        raise ValueError(f"Incomplete {label} cohort; record a fresh baseline")
    return {key: source[key] for key in fields}


def effective_cohort(report: dict) -> dict:
    """Bind scene conditions, never observed frame counts/timings or build hashes."""
    effective = required(report, EFFECTIVE, "effective")
    required(effective["diagnostics"], DIAGNOSTICS, "diagnostic")
    runtime = required(report.get("runtime_identity"), ("renderer", "effective_flags", "parameters"), "runtime")
    parameters = required(runtime["parameters"], ("multimesh_min_run",), "renderer parameters")
    if type(parameters["multimesh_min_run"]) is not int:
        raise ValueError("Invalid MultiMesh threshold")
    required(runtime["effective_flags"], FLAGS, "optimization flags")
    flags = dict(runtime["effective_flags"])
    if any(type(value) is not bool for value in flags.values()):
        raise ValueError("Invalid optimization flag values")
    rows = report.get("scenes")
    if not isinstance(rows, list) or not rows:
        raise ValueError("Missing scene cohort")
    scenes = {}
    for row in rows:
        name = row.get("scene")
        if not isinstance(name, str) or not name or name in scenes:
            raise ValueError("Missing or duplicate scene cohort")
        scenes[name] = required(row, SCENE, name)
        if type(row["minimap_open"]) is not bool:
            raise ValueError("Invalid minimap cohort state")
    return {"effective": effective, "renderer": runtime["renderer"], "parameters": parameters,
            "flags": flags, "scenes": scenes}


def read_arm(path: Path | None) -> dict | None:
    if path is None:
        return None
    arm = json.loads(path.read_text(encoding="utf-8"))
    if (not isinstance(arm, dict) or not isinstance(arm.get("name"), str)
            or not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,47}", arm["name"])):
        raise ValueError("Experiment arm requires a short lowercase name")
    flags = arm.get("flags")
    if (not isinstance(flags, dict) or not flags or not flags.keys() <= FLAGS
            or any(type(value) is not bool for value in flags.values())):
        raise ValueError("Experiment arm requires known boolean optimization flags")
    return {"name": arm["name"], "flags": flags}


def invariant_args(arguments: str, arm: dict | None) -> str:
    if arm is None:
        return arguments
    # Only known optimization switches whose entire effect was declared can vary.
    return shlex.join(arg for arg in shlex.split(arguments)
                      if not (ARG_FLAGS.get(arg) and ARG_FLAGS[arg] <= arm["flags"].keys()))


def check_arm(cohort: dict, arm: dict | None) -> None:
    for key, value in (arm or {}).get("flags", {}).items():
        if cohort["flags"][key] != value:
            raise ValueError(f"Effective {key} differs from declared experiment arm")


def compatible(before: dict, after: dict, before_arm: dict | None, after_arm: dict | None) -> None:
    check_arm(before, before_arm)
    check_arm(after, after_arm)
    varied = set((before_arm or {}).get("flags", {}))
    if varied != set((after_arm or {}).get("flags", {})):
        raise ValueError("Experiment arm dimensions differ from baseline")
    for field in ("effective", "renderer", "parameters", "scenes"):
        if before[field] != after[field]:
            raise ValueError(f"Effective cohort {field} differs from baseline; baseline unchanged")
    if {k: v for k, v in before["flags"].items() if k not in varied} != {
            k: v for k, v in after["flags"].items() if k not in varied}:
        raise ValueError("Undeclared optimization flags differ from baseline")
