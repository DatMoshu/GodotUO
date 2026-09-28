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

COLUMNS = ("mean_ms", "p95_ms", "p99_ms", "draw_calls", "batcher_items", "texture_switches", "world_draw_ms", "render_cpu_ms",
           "land_uploads_per_frame")


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
            x, y = ra[scene].get(c, 0), rb[scene].get(c, 0)  # older runs lack the newer columns
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
    ap.add_argument("--compare", nargs=2, metavar=("A", "B"), help="compare two labels already measured")
    ap.add_argument("--as-built", action="store_true",
                    help="run GUO as it is built, instead of rebuilding it optimised first (tools/guo/build.py)")
    args = ap.parse_args()
    cfg = load_config()
    out = cfg.build / "perf"
    out.mkdir(parents=True, exist_ok=True)
    if args.compare:
        return compare(out, *args.compare)

    if not args.as_built:
        build_client(cfg)

    home = out / f"client_home_{args.label}"
    (home / "cache").mkdir(parents=True, exist_ok=True)
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": cfg.shard_host,
           "UO_SHARD_PORT": str(args.port or cfg.shard_port)}
    cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--silent",
           "--window-size", args.size, "--perf-probe", "--perf-out", str(out), "--perf-label", args.label, *(["--perf-zoom", str(args.zoom)] if args.zoom else []),
           # Timed in the Classic look unless the caller names one: a saved
           # look adds its passes to every number. The report says which ran.
           *([] if "--postfx" in args.args else ["--postfx", "off"]),
           *shlex.split(args.args)]
    log = out / f"perf_{args.label}.log"
    print(f"[perf] {args.label}: shard {env['UO_SHARD_HOST']}:{env['UO_SHARD_PORT']}, window {args.size}, log {log}", flush=True)
    r = subprocess.run(cmd, stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT,
                       env=env, timeout=1800, **no_activate())
    md = out / f"perf_{args.label}.md"
    if md.exists():
        print(md.read_text(encoding="utf-8"))
    print(f"[perf] exit {r.returncode}")
    return r.returncode


if __name__ == "__main__":
    sys.exit(main())
