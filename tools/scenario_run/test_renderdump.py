"""SC12 tests: the renderdump and render_diff step kinds, on a fake client and two tiny synthetic dumps (no game data)."""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import scenario as sc  # noqa: E402
from driver import Runner  # noqa: E402
from test_client import make_client  # noqa: E402
from test_run import FakeSession, step  # noqa: E402


def obj(kind, graphic, z=0, **kw):
    return {"kind": kind, "graphic": graphic, "hue": 0, "x": 10, "y": 10, "z": z, "priority_z": z, "depth": 0.5, "alpha": 255,
            "allowed": True, "drawn": "mesh", **kw}


def dump(label, objects):
    return {"label": label, "player": {"x": 10, "y": 10, "z": 0}, "map": 0, "max_ground_z": 0, "lists": {},
            "tiles": [{"x": 10, "y": 10, "objects": objects}]}


BASE = [obj("Land", 3), obj("Static", 100, 1)]
EXTRA = BASE + [obj("Static", 200, 2)]


def write(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data), encoding="utf-8")


class DumpingClient:
    """The fake game MCP: saying `renderdump NAME` writes the run's guo.json, as the client does with GUO_RENDER_DUMP_DIR."""

    def __init__(self, inner, run_dir, objects):
        self.inner, self.run_dir, self.objects = inner, run_dir, objects

    def call(self, tool, arguments=None, timeout=45.0):
        if tool == "guo_input" and arguments.get("kind") == "text" and arguments["text"].startswith("renderdump "):
            name = arguments["text"].split()[1]
            if self.objects is not None:
                write(self.run_dir / "render_dump" / name / "guo.json", dump("guo", self.objects))
        return self.inner.call(tool, arguments, timeout)

    def __getattr__(self, name):
        return getattr(self.inner, name)


def run_steps(tmp_path, monkeypatch, steps, guo_objects, ref_objects=None, ref_env=False):
    monkeypatch.delenv("GUO_RENDER_REF_DIR", raising=False)
    runner, client, session, log, run_dir = make_client(tmp_path, steps)
    runner.fake = DumpingClient(client, run_dir, guo_objects)
    runner._connect = lambda s: runner.fake
    ref_root = runner.repo_root / "build" / "render_dump"
    if ref_env:
        ref_root = tmp_path / "elsewhere"
        monkeypatch.setenv("GUO_RENDER_REF_DIR", str(ref_root))
    if ref_objects is not None:
        write(ref_root / "spot" / "cuo.json", dump("cuo", ref_objects))
    return runner.run(), runner, session, log, run_dir


LAUNCH = step("launch", "launch")


def test_kinds_are_implemented_and_validate():
    assert {"renderdump", "render_diff"} <= sc.IMPLEMENTED
    assert sc.validate({"id": "a.b", "surface": "client", "steps": [step("d", "renderdump", name="spot"),
                                                                    step("f", "render_diff", name="spot")]}) == []
    assert sc.validate({"id": "a.b", "surface": "client", "steps": [step("d", "renderdump", nmae="spot")]})


def test_launch_sets_the_dump_dir_only_for_a_scenario_with_a_renderdump_step(tmp_path, monkeypatch):
    result, runner, session, _, run_dir = run_steps(tmp_path, monkeypatch, [LAUNCH, step("d", "renderdump", name="spot")], BASE)
    assert session.extra_env == {"GUO_RENDER_DUMP_DIR": str(run_dir / "render_dump")}
    _, _, plain, _, _ = run_steps(tmp_path, monkeypatch, [LAUNCH], None)
    assert not getattr(plain, "extra_env", {})


def test_renderdump_says_the_word_and_reports_size_and_object_count(tmp_path, monkeypatch):
    result, runner, _, log, run_dir = run_steps(tmp_path, monkeypatch, [LAUNCH, step("d", "renderdump", name="spot")], BASE)
    assert result["ok"], result["steps"]
    said = [c[1]["text"] for c in runner.fake.inner.calls if c[0] == "guo_input" and c[1].get("kind") == "text"]
    assert said == ["renderdump spot"]
    detail = [e for e in log.events if e["kind"] == "log" and e.get("step") == "d"][0]["detail"]
    assert detail["objects"] == 2 and detail["bytes"] == (run_dir / "render_dump" / "spot" / "guo.json").stat().st_size
    assert detail["dump"].endswith("render_dump/spot/guo.json")
    assert "render_dump/spot/guo.json" in result["artifacts"]


def test_renderdump_fails_when_no_dump_appears(tmp_path, monkeypatch):
    result, *_ = run_steps(tmp_path, monkeypatch, [LAUNCH, step("d", "renderdump", name="spot", )], None)
    assert not result["ok"]
    assert "no dump appeared" in result["steps"][-1]["detail"]


def test_renderdump_refuses_a_name_that_is_not_a_word(tmp_path, monkeypatch):
    result, *_ = run_steps(tmp_path, monkeypatch, [LAUNCH, step("d", "renderdump", name="../x")], BASE)
    assert not result["ok"] and "name must be" in result["steps"][-1]["detail"]


def test_render_diff_passes_on_a_match_and_writes_diff_md(tmp_path, monkeypatch):
    steps = [LAUNCH, step("d", "renderdump", name="spot"), step("f", "render_diff", name="spot",)]
    result, _, _, _, run_dir = run_steps(tmp_path, monkeypatch, steps, BASE, BASE)
    assert result["ok"], result["steps"]
    assert (run_dir / "diff.md").read_text(encoding="utf-8").startswith("# Render diff")
    assert "diff.md" in result["artifacts"]


def test_render_diff_fails_on_a_mismatch_with_the_first_lines(tmp_path, monkeypatch):
    steps = [LAUNCH, step("d", "renderdump", name="spot"), step("f", "render_diff", name="spot")]
    result, _, _, log, run_dir = run_steps(tmp_path, monkeypatch, steps, EXTRA, BASE)
    assert not result["ok"]
    end = [e for e in log.events if e["kind"] == "step_end" and e.get("step") == "f"][0]
    assert end["ok"] is False
    assert end["detail"]["map"] == 1
    assert any("GUO only" in line for line in end["detail"]["first_mismatches"])
    assert "GUO only" in (run_dir / "diff.md").read_text(encoding="utf-8")


def test_render_diff_expectation_allows_a_number_of_drawn_differences(tmp_path, monkeypatch):
    ref = [obj("Land", 3), obj("Static", 100, 1, drawn=None)]
    guo = [obj("Land", 3), obj("Static", 100, 1)]
    steps = [LAUNCH, step("d", "renderdump", name="spot"),
             step("f", "render_diff", name="spot", expect={"render_diff": {"max_drawn_diff": 1}}, on_fail="continue")]
    result, _, _, log, _ = run_steps(tmp_path, monkeypatch, steps, guo, ref)
    detail = [e for e in log.events if e["kind"] == "step_end" and e.get("step") == "f"][0]["detail"]
    assert detail["drawn"] == 1       # the step itself still fails on the mismatch; the expectation sees the count


def test_render_diff_missing_reference_names_the_path(tmp_path, monkeypatch):
    steps = [LAUNCH, step("d", "renderdump", name="spot"), step("f", "render_diff", name="spot")]
    result, *_ = run_steps(tmp_path, monkeypatch, steps, BASE, None)
    why = result["steps"][-1]["detail"]
    assert not result["ok"] and "ClassicUO reference is missing" in why and "build/render_dump/spot/cuo.json" in why


def test_render_diff_reads_the_reference_from_guo_render_ref_dir(tmp_path, monkeypatch):
    steps = [LAUNCH, step("d", "renderdump", name="spot"), step("f", "render_diff", name="spot")]
    result, *_ = run_steps(tmp_path, monkeypatch, steps, BASE, BASE, ref_env=True)
    assert result["ok"], result["steps"]
