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
from gate import compare, validate

SCHEMA = "guo/perf_gate@1"


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
            "shard": tag(f"{cfg.shard_host}:{args.port or cfg.shard_port}"), "args": args.args}


def prerequisites(cfg) -> list[str]:
    missing = []
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


def write_baseline(path: Path, context: dict, report: dict, replace: bool) -> None:
    validate(report)
    doc = {"schema": SCHEMA, "recorded": datetime.now(timezone.utc).isoformat(),
           "context": context, "report": report}
    # Only called after a successful real probe. Failed probes cannot erase a
    # previous baseline. A first record uses exclusive creation.
    if replace:
        temporary = path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        temporary.replace(path)
    else:
        with path.open("x", encoding="utf-8") as stream:
            stream.write(json.dumps(doc, indent=2) + "\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", type=slug, default="default", help="local baseline name")
    parser.add_argument("--fixture", type=slug, help="label for the shard world/data fixture; required for measurements")
    parser.add_argument("--size", type=window, default="1280,720")
    parser.add_argument("--zoom", type=float, default=1.0)
    parser.add_argument("--renderer", choices=("forward_plus", "mobile", "gl_compatibility"), default="forward_plus")
    parser.add_argument("--port", type=int)
    parser.add_argument("--args", default="", help="extra client flags; must match the baseline exactly")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--record-baseline", action="store_true", help="record a new baseline; refuses an existing one")
    modes.add_argument("--replace-baseline", action="store_true", help="deliberately replace an existing baseline after a successful probe")
    modes.add_argument("--doctor", action="store_true", help="check local prerequisites without connecting to the shard")
    args = parser.parse_args(argv)
    if not math.isfinite(args.zoom) or args.zoom <= 0:
        parser.error("--zoom must be a finite positive number")
    if args.port is not None and not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    if not args.doctor and not args.fixture:
        parser.error("--fixture is required; name the shard world/data version used for this comparison")

    try:
        cfg = load_config()
        missing = prerequisites(cfg)
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
        cmd = [sys.executable, str(Path(__file__).with_name("run.py")), "--label", label,
               "--size", args.size, "--zoom", str(args.zoom), "--renderer", args.renderer,
               "--args=" + args.args]
        if args.port:
            cmd.extend(["--port", str(args.port)])
        result = subprocess.run(cmd, check=False, env=build_child_env())
        if result.returncode:
            print("[perf_gate] Probe failed; baseline unchanged. Check its log and run launchers/dev/perf_gate --doctor.")
            return 2
        report = json.loads((cfg.build / "perf" / f"perf_{label}.json").read_text(encoding="utf-8"))
        validate(report)
        if report["window"] != context["window"]:
            raise ValueError("actual probe window differs from requested window; baseline unchanged")
        if args.record_baseline or args.replace_baseline:
            write_baseline(baseline_path, context, report, args.replace_baseline)
            print(f"[perf_gate] Baseline recorded: {baseline_path}. This is not a regression comparison.")
            return 0
        passed, summary = compare(baseline, report)
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
