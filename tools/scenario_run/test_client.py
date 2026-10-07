"""Step 3 tests: the client surface (ui.* kinds, chat, expectations), capture, the frame watchdog, the UNC fix.

The client is a fake that answers the game MCP's tools from a table, so nothing here opens a window.
"""

from __future__ import annotations

import base64
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import capture  # noqa: E402
import events as ev  # noqa: E402
import uiquery  # noqa: E402
from driver import Runner  # noqa: E402
from redact import redact  # noqa: E402
from test_run import Clock, FakeClient, FakeSession, make, scen, step  # noqa: E402

B = chr(92)
PNG = b"\x89PNG\r\n\x1a\n" + b"\0\0\0\rIHDR" + (320).to_bytes(4, "big") + (200).to_bytes(4, "big") + b"\0" * 8
LOGIN = {"scene": "LoginScene", "width": 1280, "height": 720, "controls": [
    {"system": "classic", "type": "LoginGump", "x": 0, "y": 0, "width": 640, "height": 480, "focused": False, "text": None},
    {"system": "classic", "type": "TextBox", "x": 100, "y": 200, "width": 200, "height": 20, "focused": False, "text": None},
    {"system": "classic", "type": "Button", "x": 300, "y": 400, "width": 60, "height": 20, "focused": False, "text": "Connect"},
    {"system": "godot", "type": "Label", "name": "Status", "x": 0, "y": 460, "width": 100, "height": 10, "focused": False, "text": "Ready"}]}


class ClientFake(FakeClient):
    def __init__(self, replies):
        super().__init__(replies)
        self.images = [{"type": "image", "mimeType": "image/png", "data": base64.b64encode(PNG).decode()}]

    def call_content(self, tool, arguments=None, timeout=45.0):
        self.calls.append((tool, arguments))
        return False, self.images


def make_client(tmp_path, steps, replies=None, clock=None, session=None):
    clock = clock or Clock()
    root = tmp_path / f"repo{len(list(tmp_path.glob('repo*')))}"
    run_dir = root / "build" / "runs" / "r1"
    run_dir.mkdir(parents=True)
    log = ev.EventLog(run_dir / "events.jsonl", "r1", "ai")
    session = session or FakeSession()
    client = ClientFake({"guo_state": {"frame": 7, "scene": "LoginScene", "width": 1280, "height": 720, "player": None},
                         "guo_ui": LOGIN, "guo_input": (False, "ok"), "guo_quit": (False, "Quitting."), **(replies or {})})
    runner = Runner(scen(steps, surface="client"), run_dir, log, repo_root=root, session=session,
                    connect=lambda s: client, clock=clock, sleep=clock.sleep)
    return runner, client, session, log, run_dir


def events_of(log, kind):
    return [e for e in log.events if e["kind"] == kind]


# --- selectors -------------------------------------------------------------------------------------------------

def test_selector_forms():
    assert uiquery.find(LOGIN, "Connect")["type"] == "Button"          # by text
    assert uiquery.find(LOGIN, "Status")["type"] == "Label"            # by name
    assert uiquery.find(LOGIN, {"type": "Button", "contains": "Conn"}) is not None
    assert uiquery.find(LOGIN, {"system": "godot"})["name"] == "Status"
    assert uiquery.find(LOGIN, {"type": "TextBox", "index": 1}) is None
    assert uiquery.center(uiquery.find(LOGIN, "Connect")) == (330, 410)
    for bad in ("", {}, {"colour": "red"}, 5):
        try:
            uiquery.normalize(bad)
        except uiquery.SelectorError:
            continue
        raise AssertionError(bad)


# --- the kinds -------------------------------------------------------------------------------------------------

def test_launch_ui_fill_click_chat_key(tmp_path):
    steps = [step("go", "launch", args=["--x"]),
             step("name", "ui.fill", control={"type": "TextBox"}, text="$account", clear=2),
             step("press", "ui.click", control="Connect", expect={"ui.exists": "LoginGump"}),
             step("say", "chat", text="[go 1 2"),
             step("esc", "ui.key", key="Escape", shift=True)]
    r, client, session, log, _ = make_client(tmp_path, steps)
    r.variables = {"account": "gm1"}
    out = r.run()
    assert out["ok"], out["steps"]
    assert session.started and session.stopped and client.closed
    assert session.extra_args == ["--x"]
    assert session.extra_settings == {}
    inputs = [a for t, a in client.calls if t == "guo_input"]
    assert inputs[0] == {"kind": "motion", "x": 200, "y": 210}                       # the TextBox centre
    assert {"kind": "text", "text": "gm1"} in inputs and {"kind": "text", "text": "[go 1 2"} in inputs
    assert [a["key"] for a in inputs if a["kind"] == "key"][:4] == ["BackSpace", "BackSpace", "BackSpace", "BackSpace"]
    assert {"kind": "key", "key": "Escape", "pressed": True, "shift": True} in inputs
    assert "gm1" not in log.path.read_text()          # the log holds the name, never the secret or the filled text


def test_launch_settings_reach_the_session(tmp_path):
    r, _, session, _, _ = make_client(tmp_path, [step("go", "launch", settings={"autologin": False})])
    assert r.run()["ok"]
    assert session.extra_settings == {"autologin": False}


def test_client_home_holds_the_settings_and_leaves_the_usual_file_alone(tmp_path):
    import json as _json
    import session as sess
    usual = tmp_path / "GUO"
    usual.mkdir()
    (usual / "settings.json").write_text(_json.dumps({"autologin": True, "ip": "127.0.0.1", "username": "owner"}))
    cs = sess.ClientSession(type("Cfg", (), {"cache_dir": usual / "cache", "root": tmp_path})(), tmp_path / "run", False)
    cs.extra_settings = {"autologin": False}
    cache = cs._make_home()
    home = cache.parent
    assert cache.name == "cache" and cache.is_dir() and tmp_path / "run" not in home.parents
    assert _json.loads((home / "settings.json").read_text()) == {"autologin": False, "ip": "127.0.0.1", "username": "owner"}
    assert _json.loads((usual / "settings.json").read_text())["autologin"] is True
    cs.proc = None
    cs.stop()
    assert not home.exists()


def test_home_removal_is_retried_when_the_client_still_holds_a_folder(tmp_path, monkeypatch):
    import shutil
    import session as sess
    home = tmp_path / "guo_run_home_x"
    (home / "cache" / "scratch" / "1").mkdir(parents=True)
    real, calls = shutil.rmtree, []

    def flaky(path, *a, **k):
        calls.append(path)
        if len(calls) == 1:
            raise PermissionError("held by the client")
        return real(path, *a, **k)

    monkeypatch.setattr(sess.shutil, "rmtree", flaky)
    monkeypatch.setattr(sess.time, "sleep", lambda s: None)
    assert sess.remove_tree(home)
    assert len(calls) == 2 and not home.exists()


def test_missing_control_fails_step(tmp_path):
    r, *_ = make_client(tmp_path, [step("go", "launch"), step("c", "ui.click", control="Nope", within_s=1)])
    out = r.run()
    assert not out["ok"] and "no control matches Nope" in out["steps"][1]["detail"]


def test_ui_kind_names_the_surface(tmp_path):
    r, *_ = make(tmp_path, [step("l", "launch"), step("c", "ui.click", control="X")])
    out = r.run()
    assert "drives the client" in out["steps"][1]["detail"]


def test_screenshot_decodes_the_png_and_reads_its_size(tmp_path):
    r, client, _, log, run_dir = make_client(tmp_path, [step("go", "launch"), step("s", "shot")])
    out = r.run()
    assert out["ok"] and (run_dir / "shots" / "s.png").read_bytes() == PNG
    shot = events_of(log, "shot")[0]
    assert shot["detail"]["width"] == 320 and shot["detail"]["height"] == 200 and shot["frame"] == 7


def test_events_carry_the_engine_frame(tmp_path):
    frames = [{"frame": f, "scene": "LoginScene"} for f in (1, 5, 5, 9, 9, 12, 12)]
    r, _, _, log, _ = make_client(tmp_path, [step("go", "launch"), step("n", "note", text="x")], {"guo_state": frames})
    r.run()
    seen = [e["frame"] for e in log.events if e["kind"] in ("step_start", "step_end") and e["frame"] is not None]
    assert seen == sorted(seen) and len(set(seen)) >= 2 and seen[-1] > 1


# --- expectations ----------------------------------------------------------------------------------------------

def run_expect(tmp_path, expect, replies=None):
    r, _, _, log, _ = make_client(tmp_path, [step("go", "launch"), step("e", "note", text="x", expect=expect)], replies)
    out = r.run()
    return out, events_of(log, "expect")


def test_ui_expectations(tmp_path):
    assert run_expect(tmp_path, {"ui.exists": "Connect", "ui.absent": "Paperdoll"})[0]["ok"]
    assert run_expect(tmp_path, {"ui.text": {"control": "Status", "equals": "Ready"}})[0]["ok"]
    assert run_expect(tmp_path, {"ui.text": {"control": "Status", "contains": "ead"}})[0]["ok"]
    assert run_expect(tmp_path, {"ui.count": {"control": {"type": "Button"}, "equals": 1}})[0]["ok"]
    out, ex = run_expect(tmp_path, {"ui.text": {"control": "Status", "equals": "Done"}})
    assert not out["ok"] and ex[0]["detail"]["observed"]["ui.text"] == "Ready"
    assert not run_expect(tmp_path, {"ui.exists": "Paperdoll"})[0]["ok"]


def test_world_position_with_tolerance_and_wait(tmp_path):
    here = {"frame": 1, "scene": "GameScene", "player": {"map": 0, "x": 1433, "y": 1698, "z": 5}}
    away = {"frame": 1, "scene": "GameScene", "player": None}
    out, _ = run_expect(tmp_path, {"world.position": {"x": 1434, "y": 1697, "tolerance": 2}, "within_s": 5},
                        {"guo_state": [away, away, here]})
    assert out["ok"]
    assert not run_expect(tmp_path, {"world.position": {"x": 1434, "y": 1697, "tolerance": 0}}, {"guo_state": here})[0]["ok"]
    assert not run_expect(tmp_path, {"world.position": {"x": 1433, "y": 1698, "map": 1}}, {"guo_state": here})[0]["ok"]


def test_scene_and_log_expectations(tmp_path):
    assert run_expect(tmp_path, {"scene": "LoginScene"})[0]["ok"]
    r, _, session, _, run_dir = make_client(tmp_path, [step("go", "launch"), step("e", "note", text="x", expect={"log.contains": "Connected"})])
    session.log_path = run_dir / "client.log"
    session.log_path.write_text("... Connected to shard ...")
    assert r.run()["ok"]


# --- watchdogs -------------------------------------------------------------------------------------------------

class StallSession(FakeSession):
    def __init__(self):
        super().__init__()
        self.is_stalled = False

    def stalled(self):
        return self.is_stalled


def test_frame_stall_is_a_hang(tmp_path):
    s = StallSession()
    r, _, _, log, _ = make_client(tmp_path, [step("go", "launch"), step("w", "wait", seconds=2), step("n", "note", text="x")], session=s)
    real_sleep = r._sleep

    def sleep(t):
        s.is_stalled = True
        real_sleep(t)
    r._sleep = sleep
    out = r.run()
    assert out["exit_kind"] == "hang" and "no frame was written" in out["aborted"]
    assert events_of(log, "hang") and s.stopped


def test_stall_watch_follows_file_growth(tmp_path):
    f = tmp_path / "run.avi"
    t = [0.0]
    w = capture.StallWatch(f, lambda: t[0], limit_s=20)
    assert not w.stalled()                       # no file yet: not a stall
    f.write_bytes(b"a")
    assert not w.stalled()
    t[0] = 15
    f.write_bytes(b"ab")                         # grew
    assert not w.stalled()
    t[0] = 30
    assert not w.stalled()                       # 15 s since growth
    t[0] = 36
    assert w.stalled()


def test_recording_client_is_asked_to_quit_before_it_is_killed(tmp_path):
    s = FakeSession()
    s.record = True
    s.waited = []
    s.wait_exit = lambda t: s.waited.append(t) or True
    r, client, _, _, _ = make_client(tmp_path, [step("go", "launch")], session=s)
    r.run()
    assert ("guo_quit", {}) in client.calls and s.waited == [30] and s.stopped


def test_editor_invoke_fails_fast_when_not_preapproved(tmp_path):
    s = FakeSession()
    s.preapproved = {"tour_segment", "editor_screenshot"}
    r, client, *_ = make(tmp_path, [step("l", "launch"), step("i", "editor_invoke", key="x")], session=s)
    out = r.run()
    assert not out["ok"] and "person at the PC" in out["steps"][1]["detail"]
    assert not [c for c in client.calls if c[0] == "editor_invoke"]       # nothing was sent, so nothing can hang


# --- capture ---------------------------------------------------------------------------------------------------

def test_engine_args_quote_the_movie_path():
    args = capture.engine_args(Path("a b") / "run.avi", 60)
    assert args.startswith('--write-movie "') and args.endswith('" --fixed-fps 60')


def test_preflight_names_each_problem(tmp_path, monkeypatch):
    monkeypatch.setattr(capture, "find_ffmpeg", lambda: None)
    monkeypatch.setattr(capture, "free_gb", lambda p: 2.0)
    problems = capture.preflight(record=True, build_dir=tmp_path, video_dir=str(tmp_path / "missing"), shard=("127.0.0.1", 9))
    text = " | ".join(problems)
    assert "10 GB" in text and "ffmpeg" in text and "not mounted" in text and "does not answer" in text
    assert capture.preflight(record=False, build_dir=tmp_path, video_dir="", shard=None) == []


def test_transcode_reports_missing_movie(tmp_path):
    ok, why = capture.transcode("ffmpeg", tmp_path / "none.avi", tmp_path / "o.mp4")
    assert not ok and "no movie" in why


# --- the UNC fix (PR 17 follow-up) -----------------------------------------------------------------------------

def test_unc_rule_leaves_json_escaped_relative_paths_alone():
    rel = "tools" + B * 2 + "scenario_run" + B * 2 + "run.py"            # as json.dumps writes tools\scenario_run\run.py
    assert redact(rel) == rel
    assert redact("x " + B * 2 + "srv" + B + "share" + B + "a.png") == "x <network path>"
    assert redact("x " + B * 4 + "srv" + B * 2 + "share" + B * 2 + "a.png") == "x <network path>"   # JSON-escaped UNC


# --- the run folder: master video, hard timer ------------------------------------------------------------------

class Rec:
    def __init__(self, tmp_path, record=True):
        self.record = record
        self.avi = tmp_path / "raw" / "run.avi"
        self.avi.parent.mkdir(parents=True, exist_ok=True)
        self.avi.write_bytes(b"movie")


def test_finalize_video_copies_master_and_drops_raw(tmp_path, monkeypatch):
    import run as runmod
    monkeypatch.setattr(capture, "find_ffmpeg", lambda: "ffmpeg")
    monkeypatch.setattr(capture, "transcode", lambda f, a, m, crf=16: (m.write_bytes(b"mp4") or True, "run.mp4, 1 KiB"))
    session, drive = Rec(tmp_path), tmp_path / "drive"
    drive.mkdir()
    path, note = runmod.finalize_video(session, tmp_path, "r1", str(drive))
    assert path == str(drive / "r1.mp4") and (drive / "r1.mp4").read_bytes() == b"mp4" and not session.avi.exists()
    assert "CRF 16" in note
    session2 = Rec(tmp_path)
    path, note = runmod.finalize_video(session2, tmp_path, "r2", "")      # no video folder: master stays local, row says pending
    assert path is None and "pending" in note and (tmp_path / "run.mp4").is_file()


def test_finalize_video_keeps_the_raw_movie_when_ffmpeg_fails(tmp_path, monkeypatch):
    import run as runmod
    monkeypatch.setattr(capture, "find_ffmpeg", lambda: "ffmpeg")
    monkeypatch.setattr(capture, "transcode", lambda f, a, m, crf=16: (False, "ffmpeg failed: x"))
    session = Rec(tmp_path)
    path, note = runmod.finalize_video(session, tmp_path, "r1", "")
    assert path is None and "No master" in note and session.avi.exists()
    path, note = runmod.finalize_video(Rec(tmp_path, record=False), tmp_path, "r1", "")
    assert "No video" in note


def test_hard_timer_kills_the_program_and_exits_nonzero(tmp_path, monkeypatch):
    import run as runmod
    exits = []
    monkeypatch.setattr(runmod.os, "_exit", lambda code: exits.append(code))
    monkeypatch.setattr(runmod, "HARD_KILL_GRACE_S", 0)
    s = FakeSession()
    log = ev.EventLog(tmp_path / "e.jsonl", "r1", "ai")
    timer = runmod.arm_hard_timer(scen([step("n", "note", text="x")], timeouts={"run_s": 0.05}), s, log)
    timer.join(2)
    assert exits == [4] and s.stopped and [e["kind"] for e in log.events] == ["hang"]
    timer2 = runmod.arm_hard_timer(scen([step("n", "note", text="x")], timeouts={"run_s": 0.05}), s, log)
    timer2.cancel()                                                     # a finished run disarms it


def test_shard_address_only_for_client_scenarios_that_need_one(tmp_path, monkeypatch):
    import run as runmod
    monkeypatch.setattr(runmod.settings, "read_setting", lambda k, root=None: {"UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": "2593"}.get(k, ""))
    need = scen([step("n", "note", text="x")], surface="client", requires={"shard": "editor_shard"})
    assert runmod.shard_address(need, tmp_path) == ("127.0.0.1", 2593)
    assert runmod.shard_address(scen([step("n", "note", text="x")], surface="client"), tmp_path) is None
    assert runmod.shard_address(scen([step("n", "note", text="x")], requires={"shard": "x"}), tmp_path) is None
