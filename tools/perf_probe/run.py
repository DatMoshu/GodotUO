#!/usr/bin/env python3
"""Frame time in fixed scenes (Epic B, B1): the client's --perf-probe, run and reported.

    python tools/perf_probe/run.py [--label NAME] [--port PORT] [--args "--batched-world"] [--size 1280,720]

Runs the desktop client windowed, never taking the focus, against a shard
(UO_SHARD_HOST:UO_SHARD_PORT, or --port) where the probe account is a GM. The
probe measures the login screen, then [go's to an open field, the Britain
bank, a dense forest and a dungeon, then runs from the open field through
ground that loads as it comes into view, counting the land-array uploads
(--merged-land=array, --merged-cover) on the way (src/Bootstrap/PerfProbe.cs), and writes
build/perf/perf_<label>.md and .json. --compare A B prints the two labels'
tables side by side.

The frame times only compare between runs on one machine at one window size;
the draw calls and batcher counts compare anywhere. Device owners run the same
client flags on Android, the Deck or the web: --perf-probe --perf-out DIR
--perf-label NAME.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.build import build_client  # noqa: E402
from guo.process import no_activate  # noqa: E402

# Frame times first, mean after them; FPS is left out (PERF-LANE-01).
COLUMNS = ("p50_ms", "p95_ms", "p99_ms", "mean_ms", "draw_calls", "batcher_items", "texture_switches", "world_draw_ms",
           "render_cpu_ms", "land_uploads_per_frame", "host_other_cpu_sample_max_pct")


def compare(out: Path, a: str, b: str) -> int:
    ra = {r["scene"]: r for r in json.loads((out / f"perf_{a}.json").read_text(encoding="utf-8"))["scenes"]}
    rb = {r["scene"]: r for r in json.loads((out / f"perf_{b}.json").read_text(encoding="utf-8"))["scenes"]}
    lines = [f"# {a} vs {b}", "", "| Scene | " + " | ".join(f"{c} ({a} -> {b})" for c in COLUMNS) + " |",
             "|---|" + "---:|" * len(COLUMNS)]
    for scene in ra:
        if scene not in rb:
            continue
        cells = []
        for c in COLUMNS:
            x, y = ra[scene].get(c) or 0, rb[scene].get(c) or 0  # older runs lack the newer columns
            pct = f" ({(y - x) / x * 100:+.0f}%)" if x else ""
            cells.append(f"{x} -> {y}{pct}")
        lines.append(f"| {scene} | " + " | ".join(cells) + " |")
    text = "\n".join(lines) + "\n"
    (out / f"compare_{a}_vs_{b}.md").write_text(text, encoding="utf-8")
    print(text)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--label", default="baseline")
    ap.add_argument("--port", type=int, help="shard port (default UO_SHARD_PORT)")
    ap.add_argument("--args", default="", help="extra client flags, e.g. --batched-world")
    ap.add_argument("--size", default="1280,720", help="window size; frame times compare only at one size")
    ap.add_argument("--zoom", type=float, default=0, help="camera zoom for the world scenes (0 = the profile's)")
    ap.add_argument("--renderer", choices=("forward_plus", "mobile", "gl_compatibility"),
                    help="pin the Godot rendering method before the client starts")
    ap.add_argument("--compare", nargs=2, metavar=("A", "B"), help="compare two labels already measured")
    ap.add_argument("--exe", type=Path,
                    help="run this exported GUO executable instead of the project through the editor (implies --as-built);"
                         " only an exported Release build times what players run")
    ap.add_argument("--as-built", action="store_true",
                    help="run GUO as it is built, instead of rebuilding it optimised first (tools/guo/build.py)")
    ap.add_argument("--manifest-inputs", type=Path,
                    help="private JSON configuration/fixture file maps; verify Release build and loaded runtime")
    args = ap.parse_args()
    cfg = load_config()
    out = cfg.build / "perf"
    out.mkdir(parents=True, exist_ok=True)
    if args.compare:
        return compare(out, *args.compare)

    if args.exe and not args.exe.is_file():
        ap.error("--exe must name an existing export")
    if args.manifest_inputs:
        if not args.exe:
            ap.error("--manifest-inputs requires --exe")
        if any((out / f"perf_{args.label}{suffix}").exists() for suffix in (".json", ".manifest.json", ".log")):
            ap.error("manifest runs require a fresh --label")
        from guo.perf_manifest import verify_build, cohort_inputs, measured_manifest, raw_identities
        build_identity = verify_build(args.exe)
        inputs_before = cohort_inputs(args.manifest_inputs)
    if not args.as_built and not args.exe:
        build_client(cfg)

    home = out / f"client_home_{args.label}"
    (home / "cache").mkdir(parents=True, exist_ok=True)
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": cfg.shard_host,
           "UO_SHARD_PORT": str(args.port or cfg.shard_port)}
    if args.manifest_inputs:
        env["GUO_PERF_MANIFEST"] = "1"
        env["GUO_PERF_RAW_FRAMES"] = "1"
    # The probe logs in as the owner account, whose password the launchers
    # export from the per-user secrets file; run from Python, nothing did, and
    # the shard refused it.
    for key, value in (("UO_SHARD_OWNER", cfg.shard_owner), ("UO_SHARD_OWNER_PASSWORD", cfg.shard_owner_password),
                       ("UO_SHARD_GM_ACCOUNTS", ",".join(cfg.shard_gm_accounts)), ("UO_SHARD_GM_PASSWORD", cfg.shard_gm_password)):
        if value and not env.get(key):
            env[key] = value
    engine = [str(args.exe)] if args.exe else [str(cfg.godot_console_exe), "--path", str(cfg.godot_project)]
    cmd = [*engine, *(["--rendering-method", args.renderer] if args.renderer else []), "--", "--play", "--silent",
           "--window-size", args.size, "--perf-probe", "--perf-out", str(out), "--perf-label", args.label, *(["--perf-zoom", str(args.zoom)] if args.zoom else []),
           # Timed in the Classic look unless the caller names one: a saved
           # look adds its passes to every number. The report says which ran.
           *([] if "--postfx" in args.args else ["--postfx", "off"]),
           *shlex.split(args.args)]
    log = out / f"perf_{args.label}.log"
    print(f"[perf] {args.label}: shard {env['UO_SHARD_HOST']}:{env['UO_SHARD_PORT']}, window {args.size}, log {log}", flush=True)
    r = subprocess.run(cmd, stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT,
                       env=env, timeout=1800, **no_activate())
    if args.manifest_inputs and r.returncode == 0:
        report = json.loads((out / f"perf_{args.label}.json").read_text(encoding="utf-8"))
        if verify_build(args.exe) != build_identity:
            raise ValueError("Build receipt changed during measurement")
        manifest = measured_manifest(build_identity, report, inputs_before, cohort_inputs(args.manifest_inputs),
            {"size": args.size, "zoom": args.zoom, "renderer": args.renderer, "args": args.args,
             "diagnostics": {k: env[k] for k in ("GUO_PERF_RAW_FRAMES", "GUO_PERF_MANIFEST",
                 "GUO_PERF_WARMUP_SECONDS", "GUO_PERF_MEASURE_SECONDS", "GUO_PERF_WALK_SECONDS",
                 "GUO_PERF_WALK_ORIGIN", "GUO_PERF_RUNTIME_ATTRIBUTION", "GUO_PERF_INTERVAL_COUNTERS") if k in env}})
        manifest["raw_samples"] = raw_identities(report, out)
        (out / f"perf_{args.label}.manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    md = out / f"perf_{args.label}.md"
    if md.exists():
        print(md.read_text(encoding="utf-8"))
    print(f"[perf] exit {r.returncode}")
    return r.returncode


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError) as exc:
        print(f"[perf] Readiness validation failed: {exc}. Check manifest inputs and launchers/dev/perf_gate --doctor.", file=sys.stderr)
        sys.exit(2)
