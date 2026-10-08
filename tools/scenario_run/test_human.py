"""The human driver: a fake client whose expectations turn true after a few polls, Space, Esc, ai_only, --clean,
the run folder's shape, and the stand-in person (ghost_human.py). Nothing here opens a window."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import events as ev  # noqa: E402
import ghost_human  # noqa: E402
import human as human_mod  # noqa: E402
import run as run_mod  # noqa: E402
import scenario as sc  # noqa: E402
from human import HumanRunner  # noqa: E402
from test_client import LOGIN, ClientFake  # noqa: E402
from test_run import Clock, FakeSession, scen, step  # noqa: E402

IDLE = {"skip": False, "abort": False}
GUMP = {"scene": "GameScene", "width": 1280, "height": 720, "controls": [
    {"system": "classic", "type": "PaperDollGump", "x": 10, "y": 20, "width": 200, "height": 300, "focused": False, "text": None}]}


class OverlayClient(ClientFake):
    """The game MCP's tools from a table, plus guo_overlay: a list of replies is consumed per call, the last one repeats."""

    def __init__(self, replies):
        super().__init__({"guo_overlay": IDLE, **replies})

    def call(self, tool, arguments=None, timeout=45.0):
        if tool == "guo_overlay" and isinstance(self.replies[tool], list):
            self.calls.append((tool, arguments))
            r = self.replies[tool]
            return False, json.dumps(r.pop(0) if len(r) > 1 else r[0])
        return super().call(tool, arguments, timeout)


def make_human(tmp_path, steps, replies=None, clean=False, **extra):
    clock = Clock()
    root = tmp_path / f"repo{len(list(tmp_path.glob('repo*')))}"
    run_dir = root / "build" / "runs" / "r1"
    run_dir.mkdir(parents=True)
    log = ev.EventLog(run_dir / "events.jsonl", "r1", "human")
    session = FakeSession()
    client = OverlayClient({"guo_state": {"frame": 7, "scene": "LoginScene", "width": 1280, "height": 720, "player": None},
                            "guo_ui": LOGIN, "guo_input": (False, "ok"), "guo_quit": (False, "Quitting."), **(replies or {})})
    runner = HumanRunner(scen(steps, surface="client", **extra), run_dir, log, repo_root=root, session=session,
                         connect=lambda s: client, clock=clock, sleep=clock.sleep, clean=clean)
    return runner, client, session, log, run_dir


def overlay_calls(client):
    return [a for t, a in client.calls if t == "guo_overlay"]


def kinds(log, kind):
    return [e for e in log.events if e["kind"] == kind]


def test_expectation_that_turns_true_after_some_polls_advances_by_itself(tmp_path):
    steps = [step("go", "launch"),
             step("open", "ui.key", key="P", alt=True, expect={"ui.exists": "PaperDollGump"})]
    steps[1]["say"] = "Open the paperdoll."
    r, client, session, log, _ = make_human(tmp_path, steps, {"guo_ui": [LOGIN, LOGIN, LOGIN, GUMP]})
    out = r.run()
    assert out["ok"], out["steps"]
    assert [s["ok"] for s in out["steps"]] == [True, True]
    assert not [a for t, a in client.calls if t == "guo_input"]            # `do` is the person's, never performed
    assert overlay_calls(client)[0] == {"text": "Open the paperdoll.", "step": "2/2", "hide": False}
    assert kinds(log, "expect")[-1]["ok"] is True
    assert all(e["driver"] == "human" for e in log.events)
    assert [e["kind"] for e in log.events if e["step"] == "open"][:2] == ["step_start", "action"]
    assert ("guo_overlay", {"clear": True}) in client.calls


def test_step_without_a_matching_expectation_fails_after_the_scaled_window(tmp_path):
    steps = [step("go", "launch"), step("open", "ui.key", key="P", expect={"ui.exists": "PaperDollGump"})]
    steps[1]["timeout_s"] = 10
    r, client, session, log, _ = make_human(tmp_path, steps)
    start = r._clock()
    out = r.run()
    assert not out["ok"] and out["steps"][1]["ok"] is False and out["aborted"] == "step open failed"
    assert r._clock() - start >= 10 * human_mod.HUMAN_FACTOR                # a person gets longer than a script
    assert kinds(log, "expect")[-1]["ok"] is False


def test_space_skips_the_step_and_logs_it(tmp_path):
    steps = [step("go", "launch"), step("one", "ui.key", key="P", expect={"ui.exists": "PaperDollGump"}), step("two", "note", text="done")]
    r, client, _, log, _ = make_human(tmp_path, steps, {"guo_overlay": [IDLE, {"skip": True, "abort": False}]})
    out = r.run()
    assert out["ok"]
    assert out["steps"][1] == {"id": "one", "kind": "ui.key", "ok": None, "skipped": True, "dur_ms": out["steps"][1]["dur_ms"],
                               "detail": "skipped (space)"}
    assert out["steps"][2]["ok"] is True
    assert any(e["detail"].get("skipped") == "space" for e in kinds(log, "step_end"))


def test_esc_aborts_the_run_as_a_failure(tmp_path):
    steps = [step("go", "launch"), step("one", "ui.key", key="P", expect={"ui.exists": "PaperDollGump"}), step("two", "note", text="never")]
    r, client, session, log, _ = make_human(tmp_path, steps, {"guo_overlay": [IDLE, {"skip": False, "abort": True}]})
    out = r.run()
    assert not out["ok"] and "aborted" in out["aborted"] and "Esc" in out["aborted"]
    assert out["exit_kind"] == "failed"
    assert [s["id"] for s in out["steps"]] == ["go", "one"] and out["steps"][1]["ok"] is False
    assert session.stopped


def test_ai_only_steps_are_skipped_and_logged_without_touching_the_overlay(tmp_path):
    steps = [step("only", "chat", text="[go 1 2"), step("after", "note", text="x")]
    steps[0]["ai_only"] = True
    r, client, _, log, _ = make_human(tmp_path, steps)
    out = r.run()
    assert out["ok"] and out["steps"][0]["skipped"] is True and out["steps"][0]["ok"] is None
    assert overlay_calls(client) == []                                      # no program, no overlay: nothing was shown
    assert not [a for t, a in client.calls if t == "guo_input"]
    assert any(e["detail"].get("note") == "skipped (ai_only)" for e in kinds(log, "log"))


def test_step_with_nothing_to_check_stays_for_the_dwell_then_moves_on(tmp_path):
    steps = [step("go", "launch"), step("shot_me", "wait", seconds=5)]
    steps[1]["shot"] = True
    r, client, _, log, run_dir = make_human(tmp_path, steps)
    start = r._clock()
    out = r.run()
    assert out["ok"] and r._clock() - start >= 5                            # a wait's seconds are its dwell
    assert (run_dir / "shots" / "shot_me.png").is_file()                    # `shot: true` is the runner's, as in an AI run


def test_control_of_a_click_step_is_outlined_and_clean_hides_the_overlay(tmp_path):
    steps = [step("go", "launch"), step("press", "ui.click", control="Connect")]
    r, client, _, _, _ = make_human(tmp_path, steps, clean=True)
    assert r.run()["ok"]
    calls = overlay_calls(client)
    assert calls[0]["hide"] is True
    outline = [c for c in calls if "control" in c]
    assert outline[0]["control"] == {"x": 300, "y": 400, "width": 60, "height": 20, "label": "Connect"}


def test_launch_and_note_are_still_the_runners(tmp_path):
    steps = [step("go", "launch", args=["--x"]), step("n", "note", text="hello")]
    r, client, session, log, _ = make_human(tmp_path, steps)
    assert r.run()["ok"] and session.started and session.extra_args == ["--x"]
    assert any(e["detail"].get("note") == "hello" for e in kinds(log, "log"))


# --- run.py: the options, the run folder's shape ---------------------------------------------------------------

@pytest.fixture
def cli(tmp_path, monkeypatch):
    root = tmp_path / "proj"
    (root / "tools" / "scenarios").mkdir(parents=True)
    import guo
    monkeypatch.setattr(guo, "load_config", lambda: SimpleNamespace(root=root))
    for var in ("GUO_RUNS_DB", "GUO_RUNS_SHARED_DIR", "GUO_RUNS_VIDEO_DIR"):
        monkeypatch.setenv(var, "")
    return root


def write_scenario(root, surface):
    data = {"id": f"{surface}.test.case", "surface": surface, "timeouts": {"run_s": 100},
            "steps": [step("n", "note", text="hi")]}
    (root / "tools" / "scenarios" / f"{surface}.scenario.json").write_text(json.dumps(data), encoding="utf-8")
    return data["id"]


def test_main_refuses_human_options_without_the_human_driver(cli, capsys):
    assert run_mod.main([write_scenario(cli, "client"), "--clean"]) == 2
    assert "belong to --driver human" in capsys.readouterr().err


def test_main_human_driver_follows_client_scenarios_only(cli, capsys):
    assert run_mod.main([write_scenario(cli, "editor"), "--driver", "human"]) == 2
    assert "client scenarios" in capsys.readouterr().err


def test_main_human_driver_is_not_recorded_by_the_runner(cli, capsys):
    assert run_mod.main([write_scenario(cli, "client"), "--driver", "human", "--record"]) == 2
    assert "not recorded" in capsys.readouterr().err


def test_human_run_has_the_ai_runs_shape_with_driver_human(cli, monkeypatch, capsys):
    seen = {}

    class Fake:
        def __init__(self, scen, run_dir, log, **kw):
            seen.update(kw)
            seen["run_s"] = scen.timeouts["run_s"]

        def run(self):
            return {"started": "t0", "ended": "t1", "ok": True, "aborted": None, "exit_kind": "ok", "steps": [], "artifacts": []}

    monkeypatch.setattr(run_mod.human_mod, "HumanRunner", Fake)
    monkeypatch.setattr(run_mod, "ClientSession", lambda cfg, run_dir, record, size, interactive=False: SimpleNamespace(
        record=record, interactive=interactive, stop=lambda: None))
    monkeypatch.setattr(run_mod.capture, "preflight", lambda **kw: [])
    cfg = SimpleNamespace(root=cli, build=cli / "build")
    manifest, run_dir = run_mod.execute(sc.load(sc.find(cli, write_scenario(cli, "client"))), cfg, {}, size=None, scale=1.0,
                                        register=False, driver="human", clean=True)
    assert manifest["driver"] == "human" and manifest["run_id"].endswith("_client.test.case_human")
    assert manifest["recorded"] is False and seen["run_s"] == 100 * human_mod.HUMAN_FACTOR and seen["clean"] is True
    run_json = json.loads((run_dir / "run.json").read_text(encoding="utf-8"))
    assert run_json["driver"] == "human"
    written = [json.loads(line) for line in (run_dir / "events.jsonl").read_text(encoding="utf-8").splitlines()]
    assert written and all(e["driver"] == "human" for e in written)


# --- the stand-in person -----------------------------------------------------------------------------------------

def label(text):
    return {"system": "godot", "type": "Label", "name": ghost_human.LABEL, "x": 0, "y": 0, "width": 10, "height": 10, "focused": False, "text": text}


def test_ghost_reads_the_step_label_from_the_ui_tree():
    assert ghost_human.current_step({"controls": [label("3/18")]}) == 3
    assert ghost_human.current_step({"controls": [label("")]}) == 0
    assert ghost_human.current_step(LOGIN) == 0


class LabelClient(OverlayClient):
    """Shows the overlay's step label `shown` in guo_ui next to the login controls."""

    shown = "0/4"

    def call(self, tool, arguments=None, timeout=45.0):
        if tool == "guo_ui":
            self.calls.append((tool, arguments))
            return False, json.dumps({**LOGIN, "controls": LOGIN["controls"] + [label(self.shown)]})
        return super().call(tool, arguments, timeout)


def test_ghost_performs_each_action_step_once_when_the_overlay_reaches_it(tmp_path):
    steps = [step("a", "wait", seconds=1), step("b", "ui.click", control="Connect"), step("c", "note", text="x"),
             step("d", "chat", text="[go 1 2")]
    client = LabelClient({"guo_input": (False, "ok")})
    clock = Clock()
    labels = iter(["1/4", "2/4", "4/4"])

    def sleep(seconds):                               # each wait of the ghost's lets the driver move on to the next step
        clock.sleep(seconds)
        client.shown = next(labels, "4/4")

    log = ev.EventLog(tmp_path / "ghost.jsonl", "g", "human")
    did = ghost_human.ghost(scen(steps, surface="client"), client, log, tmp_path, clock=clock, sleep=sleep, give_up_s=60)
    log.close()
    inputs = [a for t, a in client.calls if t == "guo_input"]
    assert did == 2                                                          # the click and the chat; wait and note are not actions
    assert inputs[0] == {"kind": "motion", "x": 330, "y": 410}
    assert inputs.count({"kind": "text", "text": "[go 1 2"}) == 1
    assert not any(t == "guo_overlay" for t, _ in client.calls)             # it never touches the overlay (or Space and Esc)


def test_main_clean_and_ghost_do_not_go_together(cli, capsys):
    assert run_mod.main([write_scenario(cli, "client"), "--driver", "human", "--clean", "--ghost-human"]) == 2
    assert "do not go together" in capsys.readouterr().err
