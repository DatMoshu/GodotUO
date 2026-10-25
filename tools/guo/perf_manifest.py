"""Release export and measured-runtime identity. Hashes only; never config contents."""
from __future__ import annotations

import hashlib
import json
import math
import re
import subprocess
from pathlib import Path


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def unique(paths, description: str) -> Path:
    found = sorted(p for p in paths if p.is_file())
    if len(found) != 1:
        raise ValueError(f"Expected exactly one {description}; found {len(found)}")
    return found[0]


def export_files(exe: Path) -> dict:
    exe = exe.resolve(strict=True)
    client = unique(exe.parent.glob("data_*/GUO.dll"), "export GUO.dll")
    api = unique(client.parent.rglob("GodotSharp.dll"), "export GodotSharp.dll")
    # Every exported managed/native dependency is part of the build, not only GUO.dll.
    files = [exe, *sorted(exe.parent.glob("*.dll")), *sorted(p for p in client.parent.rglob("*") if p.is_file())]
    pck = exe.with_suffix(".pck")
    if not pck.is_file():
        raise ValueError("Missing external PCK; manifest supports the repository's non-embedded Windows preset")
    files.append(pck)
    return {"files": {p.relative_to(exe.parent).as_posix(): digest(p) for p in files},
            "executable": exe.name, "client": client.relative_to(exe.parent).as_posix(),
            "godot_api": api.relative_to(exe.parent).as_posix(),
            "pack": pck.name}


def command_output(command, root: Path) -> str:
    return subprocess.check_output(command, cwd=root, text=True, stderr=subprocess.PIPE).strip()


def source_identity(root: Path) -> dict:
    return {"head": command_output(["git", "rev-parse", "HEAD"], root),
            "tree": command_output(["git", "rev-parse", "HEAD^{tree}"], root),
            "dirty": bool(command_output(["git", "status", "--porcelain", "--untracked-files=normal"], root))}


def build_inputs(root: Path, engine: Path) -> dict:
    return {"source": source_identity(root), "engine_sha256": digest(engine),
            "sdk": command_output(["dotnet", "--version"], root),
            "configs": {name: digest(root / name) for name in (
                "godot/GUO/GUO.csproj", "godot/GUO/project.godot", "godot/GUO/export_presets.cfg")}}


def record_build(exe: Path, before: dict, after: dict, command: list[str]) -> dict:
    if before != after or before["source"]["dirty"]:
        raise ValueError("Release manifest needs unchanged clean source/config/engine during export")
    if "--export-release" not in command or "--export-debug" in command:
        raise ValueError("Release manifest requires an explicit Release export")
    document = {"schema": "guo/release-build@1", "inputs": before,
                "command": command, **export_files(exe)}
    exe.with_suffix(".build.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return document


def verify_build(exe: Path) -> dict:
    doc = json.loads(exe.with_suffix(".build.json").read_text(encoding="utf-8"))
    if doc.get("schema") != "guo/release-build@1" or doc.get("inputs", {}).get("source", {}).get("dirty") is not False:
        raise ValueError("Missing clean Release build identity; re-export with --release")
    if "--export-release" not in doc.get("command", []) or "--export-debug" in doc["command"]:
        raise ValueError("Build receipt is not Release")
    inputs = doc["inputs"]
    if (any(not re.fullmatch(r"[0-9a-f]{40,64}", inputs["source"].get(k, "")) for k in ("head", "tree"))
            or not re.fullmatch(r"[0-9a-f]{64}", inputs.get("engine_sha256", ""))
            or not inputs.get("sdk") or not inputs.get("configs")):
        raise ValueError("Incomplete build identity")
    for key, value in export_files(exe).items():
        if doc.get(key) != value:
            raise ValueError(f"Export changed since build receipt: {key}")
    return doc


def cohort_inputs(spec: Path) -> dict:
    """Explicit private config/fixture files; do not serialize their contents or paths."""
    doc = json.loads(spec.read_text(encoding="utf-8"))
    result = {}
    for group in ("configuration", "fixture"):
        rows = doc.get(group)
        if not isinstance(rows, dict) or not rows:
            raise ValueError(f"Manifest input requires nonempty {group} file mapping")
        result[group] = {name: digest((spec.parent / path).resolve(strict=True)) for name, path in rows.items()}
    return result


def measured_manifest(build: dict, report: dict, before: dict, after: dict, requested: dict) -> dict:
    if before != after:
        raise ValueError("Configuration or fixture changed during measurement")
    if report.get("optimized") is not True or report.get("godot_api_jit_optimized") is not True or report.get("engine_debug_build") is not False or report.get("export_template") != "release":
        raise ValueError("Runtime is not an optimized Release export")
    runtime = report.get("runtime_identity") or {}
    for field, role in (("executable_sha256", "executable"), ("client_sha256", "client"), ("godot_api_sha256", "godot_api")):
        if runtime.get(field) != build["files"][build[role]]:
            raise ValueError(f"Loaded runtime identity mismatch: {field}")
    for field in ("client_mvid", "godot_api_mvid", "engine_version", "dotnet_runtime", "os", "renderer", "effective_flags"):
        if not runtime.get(field):
            raise ValueError(f"Missing runtime identity: {field}")
    if report.get("godot_api_mvid") != runtime["godot_api_mvid"]:
        raise ValueError("Loaded API MVID mismatch")
    return {"schema": "guo/measured-build@1", "build": build, "inputs": before,
            "requested": requested, "runtime": runtime,
            "effective": {key: report.get(key) for key in (
                "window", "zoom", "postfx", "profile", "static_world", "quiet_fixture",
                "runtime_attribution", "warmup_seconds", "measure_seconds", "walk_seconds", "walk_origin")}}


def raw_identities(report: dict, folder: Path) -> list[dict]:
    result = []
    for scene in report["scenes"]:
        name = scene.get("raw_frames")
        if not isinstance(name, str) or Path(name).name != name:
            raise ValueError("Missing or unsafe raw-frame reference")
        path = folder / name
        raw = json.loads(path.read_text(encoding="utf-8"))
        values = raw.get("frame_ms", [])
        count = scene["frames"]
        if (raw.get("schema") != "guo/raw-frames@1" or raw.get("frames") != count
                or len(values) != count or count <= 0 or raw.get("dropped") != 0
                or raw.get("storage_grew") is not False or scene.get("sample_storage_grew") is not False
                or raw.get("initial_capacity", 0) < count
                or any(type(v) not in (float, int) or not math.isfinite(v) or v <= 0 for v in values)):
            raise ValueError("Invalid raw-frame count/drop/capacity/duration")
        if not math.isclose(sum(values) / 1000, scene["elapsed_seconds"], rel_tol=1e-9, abs_tol=1e-9):
            raise ValueError("Raw samples do not match measured elapsed time")
        result.append({"scene": scene["scene"], "file": name, "sha256": digest(path), "frames": count})
    if not result:
        raise ValueError("No measured scenes")
    return result
