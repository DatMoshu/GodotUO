#!/usr/bin/env python3
"""Compare complete PerfProbe reports; counts bind, frame times are advisory."""
from __future__ import annotations

import argparse
import json
import sys
from decimal import Decimal
from pathlib import Path

SCENES = ("login", "open-field", "britain-bank", "dense-forest", "dungeon", "walk")
COUNTS = ("draw_calls", "batcher_items")
TIMES = ("mean_ms", "p95_ms", "p99_ms")


def validate(report: object) -> dict[str, dict]:
    """Refuse incomplete or idle measurements instead of reporting a false pass."""
    if not isinstance(report, dict):
        raise ValueError("report must be an object")
    if report.get("optimized") is not True:
        raise ValueError("report must come from an optimized build")
    for key in ("window", "postfx"):
        if not isinstance(report.get(key), str) or not report[key].strip():
            raise ValueError(f"report needs {key}")
    rows = report.get("scenes")
    if not isinstance(rows, list):
        raise ValueError("report needs a scenes array")
    indexed = {}
    for row in rows:
        if not isinstance(row, dict) or row.get("scene") not in SCENES:
            raise ValueError("report contains an unknown scene")
        scene = row["scene"]
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
        if row["p95_ms"] > row["p99_ms"]:
            raise ValueError(f"{scene}: p95_ms exceeds p99_ms")
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
                  "| Scene | Mean ms (baseline -> measured) | p95 ms | p99 ms |",
                  "|---|---:|---:|---:|"])
    for scene in SCENES:
        cells = [f"{before[scene][key]} -> {after[scene][key]}" for key in TIMES]
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
