"""The tour's report builder on synthetic probe and dump files."""
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from run import GUO_ONLY, MODES, PRESETS, SCENES, build_report, cuo_cpu_ms, fps  # noqa: E402


def probe(scenes: dict[str, float], draw_calls: float = 100.0) -> dict:
    return {
        "system": {"gpu": "test gpu", "cpu": "test cpu", "os": "test os"},
        "profile": "lights classic",
        "postfx": "off",
        "scenes": [{"scene": n, "mean_ms": ms, "p95_ms": ms * 1.2, "p99_ms": ms * 1.5, "draw_calls": draw_calls,
                    "batcher_items": 1000, "texture_switches": 10, "world_draw_ms": ms * 0.5,
                    "render_cpu_ms": ms * 0.8, "gpu_ms": 0.5} for n, ms in scenes.items()],
    }


def dump(render_ms: float, update_ms: float, frame_ms: float = 4.0) -> dict:
    return {"render_frame_ms": render_ms, "update_world_ms": update_ms, "frame_ms": frame_ms, "fps_cap": 250}


@pytest.fixture
def tour_dir(tmp_path: Path) -> Path:
    (tmp_path / "tour.json").write_text(json.dumps({"label": "t", "started": "2026-10-22 00:00",
                                                     "head": "abc", "cuo_pin": "def"}), encoding="utf-8")
    (tmp_path / "guo_gate_plain.json").write_text(
        json.dumps(probe({"open-field": 4.0, "britain-bank": 10.0, "login": 2.0})), encoding="utf-8")
    (tmp_path / "guo_gate_defaults.json").write_text(
        json.dumps(probe({"open-field": 5.0, "britain-bank": 12.5}, 200.0)), encoding="utf-8")
    for scene, (r, u) in {"open-field": (2.0, 0.5), "britain-bank": (6.0, 2.0)}.items():
        d = tmp_path / "cuo" / "gate" / scene
        d.mkdir(parents=True)
        (d / "cuo.json").write_text(json.dumps(dump(r, u)), encoding="utf-8")
    return tmp_path


def test_fps_helpers():
    assert fps(None) == ""
    assert fps(0) == ""
    assert fps(4.0) == "250"
    assert cuo_cpu_ms(None) is None
    assert cuo_cpu_ms(dump(6.0, 2.0)) == 8.0


def test_report_table(tour_dir: Path):
    tour = build_report(tour_dir)
    md = (tour_dir / "tour.md").read_text(encoding="utf-8")
    assert "## gate: 1280x720, zoom 1.0" in md
    assert "| britain-bank | 8.0 | 125 | 10.0 | 100 | 15.0 | 100 | 12.5 | 80 | 18.8 | 200 |" in md
    # login has no ClassicUO row and no defaults run: empty cells, not a crash.
    assert "| login |  |  | 2.0 | 500 | 3.0 | 100 | | | | |" in md
    assert "## community" not in md
    bank = tour["summary"]["gate"]["britain-bank"]
    assert bank["cuo_fps_ceiling"] == pytest.approx(125.0)
    assert bank["plain"]["batcher_items"] == 1000
    assert json.loads((tour_dir / "tour.json").read_text(encoding="utf-8"))["summary"]["gate"]["login"]["plain"]["mean_ms"] == 2.0


def test_report_lists_every_mode_and_preset():
    assert list(MODES) == ["plain", "defaults", "uoplus-max"]
    assert set(PRESETS) == {"gate", "community", "far"}
    assert len(SCENES) == 8 and GUO_ONLY == ("login", "walk")


def test_empty_dir(tmp_path: Path):
    tour = build_report(tmp_path)
    assert tour["summary"] == {}
    assert (tmp_path / "tour.md").exists()
