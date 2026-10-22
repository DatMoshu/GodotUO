"""Synthetic gate inputs exercise the CI decision, not renderer performance."""
import copy
import json

import pytest

from gate import SCENES, compare, main


def report():
    return {"optimized": True, "static_world": True, "window": "1280x720", "postfx": "Classic (0 pass(es), --postfx)",
            "scenes": [{"scene": s, "frames": 360, "draws_per_frame": 1.0,
                        "draw_calls": 100.0, "batcher_items": 20.0, "mean_ms": 10.0,
                        "p95_ms": 12.0, "p99_ms": 15.0, "tiles_moved": 30 if s == "walk" else 0}
                       for s in SCENES]}


def test_count_boundaries_and_advisory_times():
    baseline, measured = report(), report()
    for row in measured["scenes"]:
        row.update(draw_calls=105.0, mean_ms=100, p95_ms=120, p99_ms=150)
    passed, summary = compare(baseline, measured)
    assert passed
    assert "Frame times are advisory" in summary
    measured["scenes"][2]["draw_calls"] = 105.1
    passed, summary = compare(baseline, measured)
    assert not passed
    assert "britain-bank" in summary and "draw calls > +5%" in summary


def test_fractional_five_percent_boundary():
    baseline, measured = report(), report()
    baseline["scenes"][0]["draw_calls"] = 2.0
    measured["scenes"][0]["draw_calls"] = 2.1
    assert compare(baseline, measured)[0]


def test_any_batcher_increase_fails_even_when_draw_calls_improve():
    baseline, measured = report(), report()
    measured["scenes"][-1].update(draw_calls=50, batcher_items=20.1)
    passed, summary = compare(baseline, measured)
    assert not passed
    assert "batcher items increased" in summary


@pytest.mark.parametrize("bad", [None, [], {}, {"optimized": False}])
def test_invalid_document_rejected(bad):
    with pytest.raises(ValueError):
        compare(report(), bad)


@pytest.mark.parametrize("side", ["baseline", "report"])
@pytest.mark.parametrize("mutation", ["missing", "duplicate", "malformed", "idle", "short", "stationary",
                                      "unoptimized", "dynamic", "window", "postfx"])
def test_incomplete_or_incomparable_measurement_rejected(side, mutation):
    baseline, measured = report(), report()
    doc = baseline if side == "baseline" else measured
    if mutation == "missing":
        doc["scenes"].pop()
    elif mutation == "duplicate":
        doc["scenes"].append(copy.deepcopy(doc["scenes"][0]))
    elif mutation == "malformed":
        doc["scenes"].append({"frames": 360})
    elif mutation == "dynamic":
        doc["static_world"] = False
    elif mutation == "idle":
        doc["scenes"][0]["draws_per_frame"] = 0.94
    elif mutation == "short":
        doc["scenes"][0]["frames"] = 359
    elif mutation == "stationary":
        doc["scenes"][-1]["tiles_moved"] = 0
    elif mutation == "unoptimized":
        doc["optimized"] = False
    else:
        doc[mutation] = "different"
    with pytest.raises(ValueError):
        compare(baseline, measured)


def test_extra_probe_scenes_are_ignored():
    """A stock probe report carries scenes the gate does not bind on (PERF-V5)."""
    baseline, measured = report(), report()
    for doc in (baseline, measured):
        doc["scenes"].append({"scene": "britain-docks", "frames": 360, "draws_per_frame": 1.0,
                              "draw_calls": 500.0, "batcher_items": 90.0, "mean_ms": 30.0, "p95_ms": 40.0, "p99_ms": 50.0})
    measured["scenes"][-1]["batcher_items"] = 900.0
    passed, summary = compare(baseline, measured)
    assert passed
    assert "britain-docks" not in summary


@pytest.mark.parametrize("metric", ["draw_calls", "batcher_items", "mean_ms", "p95_ms", "p99_ms", "draws_per_frame"])
@pytest.mark.parametrize("value", [None, True, "100", -1, 0, float("nan"), float("inf"), 10**400])
def test_invalid_metric_rejected(metric, value):
    measured = report()
    measured["scenes"][0][metric] = value
    with pytest.raises(ValueError):
        compare(report(), measured)


def test_cli_exit_codes_and_summary(tmp_path):
    baseline, measured, summary = (tmp_path / name for name in ("baseline.json", "report.json", "summary.md"))
    baseline.write_text(json.dumps(report()), encoding="utf-8")
    measured.write_text(json.dumps(report()), encoding="utf-8")
    args = ["--baseline", str(baseline), "--report", str(measured), "--summary", str(summary)]
    assert main(args) == 0
    doc = report()
    doc["scenes"][0]["batcher_items"] += 1
    measured.write_text(json.dumps(doc), encoding="utf-8")
    assert main(args) == 1
    measured.write_text("{broken", encoding="utf-8")
    assert main(args) == 2
    assert "**INVALID**" in summary.read_text(encoding="utf-8")
    assert str(tmp_path) not in summary.read_text(encoding="utf-8")


def test_cli_oversized_integer_is_invalid_input(tmp_path, capsys):
    baseline, measured = tmp_path / "baseline.json", tmp_path / "report.json"
    baseline.write_text(json.dumps(report()), encoding="utf-8")
    doc = report()
    doc["scenes"][0]["draw_calls"] = 10**400
    measured.write_text(json.dumps(doc), encoding="utf-8")

    assert main(["--baseline", str(baseline), "--report", str(measured)]) == 2
    assert "**INVALID**" in capsys.readouterr().out
