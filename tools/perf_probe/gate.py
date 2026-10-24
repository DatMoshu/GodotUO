#!/usr/bin/env python3
"""Compare complete PerfProbe reports; counts bind, frame times are advisory."""
from __future__ import annotations

import argparse
import json
import sys
from decimal import Decimal
from pathlib import Path

# The six scenes the gate binds on. The probe measures more (the water
# scenes, the crypt, the open sea); a report that carries them is complete,
# and their rows are ignored here (PERF-V5).
SCENES = ("login", "open-field", "britain-bank", "dense-forest", "dungeon", "walk")
COUNTS = ("draw_calls", "batcher_items")
# Frame times first, mean last; FPS is not shown (PERF-LANE-01): a mean, and
# the FPS derived from it, hide the slow frames a player feels.
TIMES = ("p50_ms", "p95_ms", "p99_ms", "mean_ms")
# Over a scene's sampled seconds, CPU load this client did not cause above
# this share marks its frame times as taken on a shared host: not quotable.
SHARED_HOST_PCT = 10.0


def host_cell(row: dict) -> str:
    """The sampled seconds' other-process CPU, or n/a when the client could not read it."""
    mean, peak = row.get("host_other_cpu_sample_mean_pct"), row.get("host_other_cpu_sample_max_pct")
    if not isinstance(peak, (int, float)) or isinstance(peak, bool):
        return "n/a"
    flag = " shared" if peak > SHARED_HOST_PCT else ""
    return f"{mean}/{peak}%{flag}"


def validate(report: object) -> dict[str, dict]:
    """Refuse incomplete or idle measurements instead of reporting a false pass."""
    if not isinstance(report, dict):
        raise ValueError("report must be an object")
    if report.get("optimized") is not True:
        raise ValueError("report must come from an optimized build")
    # Only an exported Release template times what players run: the editor
    # binary loads a debug engine, and a debug export is what
    # launchers/windows/export produced (review E, PERF-LANE-01).
    if report.get("export_template") != "release" or report.get("engine_debug_build") is not False:
        raise ValueError("report must come from an exported Release build (export_template release, engine_debug_build false)")
    # Weather and server text moved thousands of pixels between two launches.
    if report.get("quiet_fixture") is not True:
        raise ValueError("report must be measured with --perf-quiet")
    # Counts bind, so what is counted must depend on the map alone: the
    # probe's --perf-static-world leaves out every mobile but the player,
    # every item and every effect (PERF-V6).
    if report.get("static_world") is not True:
        raise ValueError("report must be measured with --perf-static-world")
    for key in ("window", "postfx"):
        if not isinstance(report.get(key), str) or not report[key].strip():
            raise ValueError(f"report needs {key}")
    rows = report.get("scenes")
    if not isinstance(rows, list):
        raise ValueError("report needs a scenes array")
    indexed = {}
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get("scene"), str):
            raise ValueError("report contains a malformed scene row")
        scene = row["scene"]
        if scene not in SCENES:
            continue
        if scene in indexed:
            raise ValueError(f"duplicate scene: {scene}")
        frames = row.get("frames")
        minimum_frames = 1 if scene == "walk" else 360
        if type(frames) is not int or frames < minimum_frames:
            raise ValueError(f"{scene}: needs at least {minimum_frames} measured frames")
        for key in (*COUNTS, *TIMES, "draws_per_frame"):
            value = row.get(key)
            # Bound metrics to the finite float range without converting:
            # JSON integers can overflow a float conversion.
            if type(value) not in (int, float) or not 0 < value <= sys.float_info.max:
                raise ValueError(f"{scene}: {key} must be a finite positive number")
        if row["draws_per_frame"] < 0.95:
            raise ValueError(f"{scene}: fewer than 0.95 draws per measured frame")
        if not row["p50_ms"] <= row["p95_ms"] <= row["p99_ms"]:
            raise ValueError(f"{scene}: frame-time percentiles out of order")
        if scene == "walk":
            tiles = row.get("tiles_moved")
            if type(tiles) is not int or tiles <= 0:
                raise ValueError("walk: probe must have moved at least one tile")
        indexed[scene] = row
    if set(indexed) != set(SCENES):
        raise ValueError("missing scenes: " + ", ".join(s for s in SCENES if s not in indexed))
    return indexed


def compare(baseline: dict, measured: dict) -> tuple[bool, str]:
    before, after = validate(baseline), validate(measured)
    for key in ("window", "postfx"):
        if baseline[key] != measured[key]:
            raise ValueError(f"{key} differs from baseline")
    lines = ["## Performance count gate", "",
             "Binding: draw calls may increase by at most 5%; batcher items may not increase.",
             "Frame times are advisory; they do not affect this count gate.", "",
             "| Scene | Draw calls (baseline -> measured) | Batcher items (baseline -> measured) | Result |",
             "|---|---:|---:|---|"]
    passed = True
    for scene in SCENES:
        a, b = before[scene], after[scene]
        # Decimal avoids rejecting a value exactly at the 5% boundary because
        # of binary floating-point rounding of the probe's one-decimal counts.
        draws_ok = Decimal(str(b["draw_calls"])) <= Decimal(str(a["draw_calls"])) * Decimal("1.05")
        items_ok = b["batcher_items"] <= a["batcher_items"]
        ok = draws_ok and items_ok
        passed &= ok
        failures = []
        if not draws_ok:
            failures.append("draw calls > +5%")
        if not items_ok:
            failures.append("batcher items increased")
        result = "PASS" if ok else "FAIL: " + "; ".join(failures)
        lines.append(f"| {scene} | {a['draw_calls']} -> {b['draw_calls']} | "
                     f"{a['batcher_items']} -> {b['batcher_items']} | {result} |")
    lines.extend(["", "### Frame times (advisory)", "",
                  "Baseline -> measured. Other CPU is the host load this client did not cause, mean/max over the",
                  f"sampled seconds only; above {SHARED_HOST_PCT:g}% the row is marked shared and its times are not quotable.", "",
                  "| Scene | p50 ms | p95 ms | p99 ms | mean ms | other CPU (baseline -> measured) |",
                  "|---|---:|---:|---:|---:|---|"])
    for scene in SCENES:
        cells = [f"{before[scene][key]} -> {after[scene][key]}" for key in TIMES]
        cells.append(f"{host_cell(before[scene])} -> {host_cell(after[scene])}")
        lines.append(f"| {scene} | " + " | ".join(cells) + " |")
    lines.extend(["", "**Count gate: " + ("PASS" if passed else "FAIL") + "**", ""])
    return passed, "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--summary", type=Path, help="append Markdown to a CI step summary")
    args = parser.parse_args(argv)
    try:
        baseline = json.loads(args.baseline.read_text(encoding="utf-8-sig"))
        report = json.loads(args.report.read_text(encoding="utf-8-sig"))
        passed, summary = compare(baseline, report)
        code = 0 if passed else 1
    except (OSError, ValueError) as exc:
        # Do not echo input documents or file paths into a public CI summary.
        reason = str(exc) if isinstance(exc, ValueError) and not isinstance(exc, json.JSONDecodeError) else "cannot read valid report JSON"
        summary = f"## Performance count gate\n\n**INVALID**: {reason}.\n"
        code = 2
    print(summary)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as stream:
            stream.write(summary + "\n")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
