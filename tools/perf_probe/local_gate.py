#!/usr/bin/env python3
"""Run the real probe and compare against an explicitly recorded local baseline."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import platform
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config
from guo.process import build_child_env
from guo.perf_cohort import read_arm, invariant_args, effective_cohort, check_arm, compatible
from gate import SCENES, TIMES, compare, host_cell, validate

SCHEMA = "guo/perf_gate@1"

# What the gate itself asks of the probe, beyond the caller's --args: the six
# scenes it binds on (login is measured whatever the list says), the map-only
# tile walk, so a stock run exits 0 on a clean main (PERF-V5) and a passer-by
# cannot move a count between identical runs (PERF-V6), and the quiet
# fixture, so rain and server text cannot either (PERF-LANE-01).
GATE_FLAGS = ["--perf-static-world", "--perf-quiet"] + [flag for scene in SCENES if scene != "login" for flag in ("--perf-scene", scene)]


def tag(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def slug(value: str) -> str:
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,47}", value):
        raise argparse.ArgumentTypeError("use 1-48 lowercase letters, digits, underscores or hyphens")
    return value


def window(value: str) -> str:
    if not re.fullmatch(r"[0-9]+,[0-9]+", value) or not all(64 <= int(n) <= 8192 for n in value.split(",")):
        raise argparse.ArgumentTypeError("use WIDTH,HEIGHT with each dimension between 64 and 8192")
    return value


def context_for(args, cfg) -> dict:
    return {"machine": tag(platform.node() + "|" + platform.system() + "|" + platform.machine()),
            "window": args.size.replace(",", "x"), "zoom": args.zoom, "renderer": args.renderer,
            "client_version": cfg.client_version, "fixture": args.fixture,
            "shard": tag(f"{cfg.shard_host}:{args.port or cfg.shard_port}"),
            "args": invariant_args(args.args, args.arm),
            "experiment_dimensions": sorted((args.arm or {}).get("flags", {})),
            "gate_flags": " ".join(GATE_FLAGS)}


def export_dll(exe: Path) -> Path | None:
    """The GUO assembly an export loads: data_<name>_<platform>/GUO.dll beside it."""
    matches = sorted(exe.parent.glob("data_*/GUO.dll"))
    return matches[0] if len(matches) == 1 else None


def provenance(cfg, exe: Path) -> dict:
    """What was measured: the checkout's head, whether it was clean, and the client DLL's hash.

    Recorded beside a baseline so a number can be traced to the exact build
    it came from; never binding, since a comparison is about the context."""
    def git(*words: str) -> str:
        # Popen, not run: the probe launch goes through run, and this must
        # never be mistaken for a launch (the tests count those).
        try:
            with subprocess.Popen(["git", *words], cwd=cfg.root, text=True, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL) as process:
                out, _ = process.communicate(timeout=30)
                return out.strip() if process.returncode == 0 else ""
        except (OSError, subprocess.SubprocessError, ValueError):
            return ""

    head = git("rev-parse", "HEAD")
    dll = export_dll(exe)
    try:
        digest = hashlib.sha256(dll.read_bytes()).hexdigest() if dll else ""
    except OSError:
        digest = ""
    return {"head": head or "unknown", "dirty": bool(git("status", "--porcelain")) if head else None,
            "client_dll_sha256": digest or "unknown"}


def prerequisites(cfg, exe: Path | None = None) -> list[str]:
    missing = []
    if exe is not None and (not exe.is_file() or export_dll(exe) is None):
        missing.append("--exe must name an exported GUO executable with its data_*/GUO.dll beside it:"
                       " export with launchers/windows/export (Release, not --export-debug)")
    if not cfg.godot_console_exe.is_file():
        missing.append("Godot .NET engine missing: run launchers/pipeline/00_bootstrap")
    if not cfg.client_data.is_dir():
        missing.append("client install missing: configure UO_CLIENT_DATA and run launchers/pipeline/01_verify_client_data")
    if cfg.build.resolve().is_relative_to(cfg.root.resolve()):
        missing.append("set UO_BUILD_DIR outside the checkout in config.local; local baselines must never be committed")
    return missing


def read_baseline(path: Path, context: dict) -> dict:
    doc = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(doc, dict) or doc.get("schema") != SCHEMA:
        raise ValueError("baseline format is unsupported")
    if doc.get("context") != context:
        raise ValueError("baseline settings differ; use the same settings or a different --name")
    validate(doc.get("report"))
    return doc["report"]


def write_baseline(path: Path, context: dict, report: dict, replace: bool, provenance: dict | None = None) -> None:
    validate(report)
    doc = {"schema": SCHEMA, "recorded": datetime.now(timezone.utc).isoformat(),
           "context": context, "provenance": provenance or {}, "report": report}
    # Only called after a successful real probe. Failed probes cannot erase a
    # previous baseline. A first record uses exclusive creation.
    if replace:
        temporary = path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        temporary.replace(path)
    else:
        with path.open("x", encoding="utf-8") as stream:
            stream.write(json.dumps(doc, indent=2) + "\n")


def probe_command(args, label: str) -> list[str]:
    cmd = [sys.executable, str(Path(__file__).with_name("run.py")), "--label", label, "--exe", str(args.exe),
           "--size", args.size, "--zoom", str(args.zoom), "--renderer", args.renderer,
           "--args=" + " ".join([*GATE_FLAGS, args.args]).strip()]
    if args.port:
        cmd.extend(["--port", str(args.port)])
    if getattr(args, "manifest_inputs", None):
        cmd.extend(["--manifest-inputs", str(args.manifest_inputs)])
    return cmd


def dry_run(args) -> int:
    """No launch: the probe command, and optionally what the gate makes of an existing report."""
    print("[perf_gate] Dry run; nothing launched. Probe command:")
    print("  " + " ".join(probe_command(args, "gate_<fresh label>")))
    if not args.from_report:
        return 0
    try:
        report = json.loads(args.from_report.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        print("[perf_gate] Cannot read that report as JSON.")
        return 2
    if not isinstance(report, dict):
        print("[perf_gate] That report is not a probe report.")
        return 2
    print()
    print("| Scene | p50 ms | p95 ms | p99 ms | mean ms | other CPU mean/max over the sample |")
    print("|---|---:|---:|---:|---:|---|")
    for row in report.get("scenes") or []:
        if isinstance(row, dict):
            print(f"| {row.get('scene')} | " + " | ".join(str(row.get(key, "n/a")) for key in TIMES)
                  + f" | {host_cell(row)} |")
    print()
    print(f"export_template {report.get('export_template', 'n/a')}, engine_debug_build {report.get('engine_debug_build', 'n/a')},"
          f" quiet_fixture {report.get('quiet_fixture', 'n/a')}, host load {(report.get('host_load') or {}).get('host_load_source', 'n/a')}")
    try:
        validate(report)
        print("[perf_gate] This report could bind.")
    except ValueError as exc:
        print(f"[perf_gate] This report cannot bind: {exc}.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", type=slug, default="default", help="local baseline name")
    parser.add_argument("--fixture", type=slug, help="label for the shard world/data fixture; required for measurements")
    parser.add_argument("--size", type=window, default="1280,720")
    parser.add_argument("--zoom", type=float, default=1.0)
    parser.add_argument("--renderer", choices=("forward_plus", "mobile", "gl_compatibility"), default="forward_plus")
    parser.add_argument("--port", type=int)
    parser.add_argument("--args", default="", help="extra client flags; must match the baseline exactly")
    parser.add_argument("--exe", type=Path, help="the exported Release GUO executable to time; required for a measurement")
    parser.add_argument("--manifest-inputs", type=Path, help="require verified build/runtime identity and raw frames")
    parser.add_argument("--experiment-arm", type=Path,
                        help="JSON name/flags declaration for intentional optimization changes; requires manifests")
    parser.add_argument("--dry-run", action="store_true",
                        help="check the export and print the probe command without launching anything")
    parser.add_argument("--from-report", type=Path,
                        help="with --dry-run: print this existing probe report's frame-time table and gate verdict")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--record-baseline", action="store_true", help="record a new baseline; refuses an existing one")
    modes.add_argument("--replace-baseline", action="store_true", help="deliberately replace an existing baseline after a successful probe")
    modes.add_argument("--doctor", action="store_true", help="check local prerequisites without connecting to the shard")
    args = parser.parse_args(argv)
    try:
        args.arm = read_arm(args.experiment_arm)
    except (OSError, ValueError) as exc:
        parser.error(str(exc))
    if args.arm and not args.manifest_inputs:
        parser.error("--experiment-arm requires --manifest-inputs")
    if not math.isfinite(args.zoom) or args.zoom <= 0:
        parser.error("--zoom must be a finite positive number")
    if args.port is not None and not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    if not args.doctor and not args.fixture:
        parser.error("--fixture is required; name the shard world/data version used for this comparison")
    if not args.doctor and not args.exe:
        parser.error("--exe is required; frame times bind only on an exported Release build (PERF-LANE-01)")
    if args.from_report and not args.dry_run:
        parser.error("--from-report only goes with --dry-run")

    try:
        cfg = load_config()
        missing = prerequisites(cfg, None if args.doctor else args.exe)
    except (OSError, ValueError, RuntimeError):
        print("[perf_gate] Cannot read local configuration. Check config.local and run launchers/dev/perf_gate --doctor.")
        return 2
    if missing:
        print("[perf_gate] " + "; ".join(missing))
        return 2
    if args.doctor:
        print("[perf_gate] Engine and client directory exist. The real probe also needs the .NET SDK,")
        print("a running shard and its GM probe account. No network connection was attempted.")
        return 0

    context = context_for(args, cfg)
    if args.dry_run:
        return dry_run(args)
    out = cfg.build / "perf" / "gate" / context["machine"]
    baseline_path = out / f"{args.name}.baseline.json"
    baseline = None
    try:
        out.mkdir(parents=True, exist_ok=True)
        if args.record_baseline and baseline_path.exists():
            raise ValueError("baseline already exists; choose a different --name or explicitly --replace-baseline")
        if not args.record_baseline:
            if not baseline_path.is_file():
                raise ValueError("no baseline for this machine/name; first run with --record-baseline")
            # A deliberate replacement may change the settings; comparison may not.
            if not args.replace_baseline:
                baseline = read_baseline(baseline_path, context)

        label = "gate_" + uuid.uuid4().hex
        cmd = probe_command(args, label)
        result = subprocess.run(cmd, check=False, env=build_child_env())
        if result.returncode:
            print("[perf_gate] Probe failed; baseline unchanged. Check its log and run launchers/dev/perf_gate --doctor.")
            return 2
        report = json.loads((cfg.build / "perf" / f"perf_{label}.json").read_text(encoding="utf-8"))
        validate(report)
        if report["window"] != context["window"]:
            raise ValueError("actual probe window differs from requested window; baseline unchanged")
        measured = provenance(cfg, args.exe)
        if args.manifest_inputs:
            manifest_path = cfg.build / "perf" / f"perf_{label}.manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            cohort = effective_cohort(report)
            if manifest.get("cohort") != cohort:
                raise ValueError("manifest cohort missing or differs from report")
            if report["zoom"] != args.zoom or cohort["renderer"] != args.renderer:
                raise ValueError("actual zoom/renderer differs from requested cohort")
            for scene, state in cohort["scenes"].items():
                if (state["window"] != context["window"] or state["renderer"] != args.renderer
                        or scene != "login" and state["zoom"] != args.zoom):
                    raise ValueError(f"actual {scene} window/zoom/renderer differs from requested cohort")
            check_arm(cohort, args.arm)
            measured["experiment_arm"] = args.arm
            measured["manifest"] = manifest
            measured["head"] = manifest["build"]["inputs"]["source"]["head"]
            measured["dirty"] = False
            if baseline is not None:
                old = json.loads(baseline_path.read_text(encoding="utf-8")).get("provenance", {}).get("manifest")
                if old is None or old.get("inputs") != manifest.get("inputs"):
                    raise ValueError("baseline lacks matching configuration/fixture manifest; use a separate baseline")
                old_cohort = effective_cohort(baseline)
                if old.get("cohort") != old_cohort:
                    raise ValueError("baseline manifest lacks matching effective cohort; record a fresh baseline")
                old_provenance = json.loads(baseline_path.read_text(encoding="utf-8"))["provenance"]
                compatible(old_cohort, cohort, old_provenance.get("experiment_arm"), args.arm)
                # Requested diagnostic switches bind too, even if an effective default currently matches.
                if old.get("requested", {}).get("diagnostics") != manifest.get("requested", {}).get("diagnostics"):
                    raise ValueError("requested diagnostic modes differ from baseline")
        if args.record_baseline or args.replace_baseline:
            write_baseline(baseline_path, context, report, args.replace_baseline, measured)
            print(f"[perf_gate] Baseline recorded: {baseline_path} (head {measured['head']}"
                  f"{', dirty tree' if measured['dirty'] else ''}). This is not a regression comparison.")
            return 0
        passed, summary = compare(baseline, report)
        recorded = json.loads(baseline_path.read_text(encoding="utf-8")).get("provenance") or {}
        summary += (f"\nBaseline head {recorded.get('head', 'unknown')}, measured head {measured['head']}"
                    f"{' (dirty tree)' if measured['dirty'] else ''}.\n")
        if args.arm:
            summary += "Declared experiment arms: " + json.dumps({"baseline": recorded.get("experiment_arm"),
                                                                 "measured": args.arm}, sort_keys=True) + "\n"
        summary_path = out / f"{args.name}.{label}.md"
        summary_path.write_text(summary, encoding="utf-8")
        print(summary)
        print(f"[perf_gate] Summary: {summary_path}")
        return 0 if passed else 1
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        print(f"[perf_gate] Cannot complete comparison: {exc}. Run launchers/dev/perf_gate --doctor.")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
