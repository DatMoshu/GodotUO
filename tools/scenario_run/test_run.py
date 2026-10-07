"""Runner tests: scenario parsing, step execution, expectations, the three watchdogs, the record. No editor needed."""

from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import events as ev  # noqa: E402
import publish  # noqa: E402
import registry  # noqa: E402
import scenario as sc  # noqa: E402
from driver import Runner  # noqa: E402
from mcp_client import McpError, McpTimeout  # noqa: E402
from redact import redact  # noqa: E402


WIN = "C" + ":\\"   # a Windows drive prefix, built at run time so no machine path sits in the file


def scen(steps, surface="editor", **extra):
    return sc.Scenario({"id": "editor.test.case", "surface": surface, "steps": steps, **extra})


def step(sid, kind, **kw):
    expect = kw.pop("expect", None)
    s = {"id": sid, "do": {"kind": kind, **kw}}
    if expect is not None:
        s["expect"] = expect
    return s


class FakeSession:
    def __init__(self, alive=True):
        self.started = self.stopped = False
        self._alive = alive

    def start(self): self.started = True
    def wait_listening(self, t): return True
    def alive(self): return self._alive and not self.stopped
    def stop(self): self.stopped = True


class FakeClient:
    """Answers tools from a table; a value that is an exception is raised, a list is consumed one per call."""

    def __init__(self, replies):
        self.replies = replies
        self.calls = []
        self.closed = False

    def call(self, tool, arguments=None, timeout=45.0):
        self.calls.append((tool, arguments))
        r = self.replies[tool]
        if isinstance(r, list):
            r = r.pop(0) if len(r) > 1 else r[0]
        if isinstance(r, Exception):
            raise r
        if isinstance(r, tuple):
            return r
        return False, json.dumps(r)

    def close(self): self.closed = True


class Clock:
    def __init__(self): self.now = 0.0
    def __call__(self): return self.now
    def sleep(self, s): self.now += s


def make(tmp_path, steps, replies=None, clock=None, session=None, **extra):
    clock = clock or Clock()
    root = tmp_path / "repo"
    run_dir = root / "build" / "runs" / "r1"
    run_dir.mkdir(parents=True)
    log = ev.EventLog(run_dir / "events.jsonl", "r1", "ai")
    session = session or FakeSession()
    client = FakeClient({"editor_state": {"assetsReady": True}, **(replies or {})})
    r = Runner(scen(steps, **extra), run_dir, log, repo_root=root, session=session, connect=lambda s: client,
               clock=clock, sleep=clock.sleep)
    return r, client, session, log, run_dir


# --- scenario parsing -------------------------------------------------------------------------------------------

def test_validate_good_and_bad():
    assert sc.validate({"id": "a.b", "surface": "editor", "steps": [step("x", "note")]}) == []
    assert any("id" in p for p in sc.validate({"id": "Bad", "surface": "editor", "steps": [step("x", "note")]}))
    assert any("duplicate" in p for p in sc.validate({"id": "a.b", "surface": "editor", "steps": [step("x", "note"), step("x", "note")]}))
    assert any("do.kind" in p for p in sc.validate({"id": "a.b", "surface": "editor", "steps": [step("x", "teleport")]}))
    assert any("surface" in p for p in sc.validate({"id": "a.b", "surface": "moon", "steps": [step("x", "note")]}))
    assert any("timeouts" in p for p in sc.validate({"id": "a.b", "surface": "editor", "timeouts": {"step_s": 0}, "steps": [step("x", "note")]}))


def test_substitute_uses_vars_then_env_and_refuses_unknown(monkeypatch):
    monkeypatch.setenv("GUO_SCENARIO_ACCOUNT", "from_env")
    assert sc.substitute({"t": ["$account", "${n}"]}, {"n": "1"}) == {"t": ["from_env", "1"]}
    assert sc.substitute("$account", {"account": "arg"}) == "arg"
    with pytest.raises(sc.ScenarioError):
        sc.substitute("$nobody_defined_this", {})


def test_subset():
    assert sc.subset({"a": 1}, {"a": 1, "b": 2})
    assert not sc.subset({"a": 1}, {"a": 2})
    assert sc.subset({"a": {"b": 1}}, {"a": {"b": 1, "c": 2}})


def test_event_log_schema_and_run_id(tmp_path):
    log = ev.EventLog(tmp_path / "e.jsonl", "rid", "ai")
    log.frame = 3
    log.emit("step_end", "s", True, {"x": 1}, dur_ms=5)
    with pytest.raises(ValueError):
        log.emit("nonsense")
    log.close()
    row = json.loads((tmp_path / "e.jsonl").read_text().splitlines()[0])
    assert {"ts", "run_id", "step", "kind", "driver", "ok", "detail", "frame", "dur_ms"} <= row.keys()
    assert row["frame"] == 3 and row["ts"].endswith("Z")
    assert ev.make_run_id("editor.tabs sweep", "ai").endswith("_editor.tabs-sweep_ai")


# --- running ----------------------------------------------------------------------------------------------------

def test_note_wait_launch_pass_and_program_stopped(tmp_path):
    r, client, session, log, _ = make(tmp_path, [step("go", "launch"), step("n", "note", text="hi"), step("w", "wait", seconds=2)])
    out = r.run()
    assert out["ok"] and out["exit_kind"] == "ok" and [s["ok"] for s in out["steps"]] == [True] * 3
    assert session.started and session.stopped and client.closed


def test_tour_segment_waits_on_running_and_records_frames(tmp_path):
    seg = {"state": "done", "ok": True, "passed": ["a"], "failures": [], "skipped": None,
           "frames": [{"file": "build/runs/r1/tour/layout/frames/f1.png", "seconds": 2}]}
    r, client, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="layout")],
                         {"tour_segment": [{"state": "running", "id": "layout"}, seg]})
    out = r.run()
    assert out["ok"]
    tour_calls = [c for c in client.calls if c[0] == "tour_segment"]
    assert tour_calls[0][1]["id"] == "layout" and tour_calls[0][1]["out_dir"] == "build/runs/r1/tour/layout"
    assert out["artifacts"] == ["tour/layout/frames/f1.png"]


def test_tour_segment_failure_fails_step_and_aborts(tmp_path):
    bad = {"state": "done", "ok": False, "failures": ["no tab"], "frames": []}
    r, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x"), step("after", "note", text="n")], {"tour_segment": bad})
    out = r.run()
    assert not out["ok"] and out["exit_kind"] == "failed" and out["steps"][-1]["id"] == "t"
    assert out["steps"][-1]["detail"] == "no tab"


def test_on_fail_continue_runs_the_rest(tmp_path):
    bad = {"state": "done", "ok": False, "failures": ["no tab"], "frames": []}
    first = step("t", "tour_segment", id="x")
    first["on_fail"] = "continue"
    r, *_ = make(tmp_path, [step("go", "launch"), first, step("after", "note", text="n")], {"tour_segment": bad})
    out = r.run()
    assert [s["ok"] for s in out["steps"]] == [True, False, True] and not out["ok"]


def test_skipped_segment_is_not_a_failure(tmp_path):
    skip = {"state": "done", "ok": False, "skipped": "no shard", "failures": [], "frames": []}
    r, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="live")], {"tour_segment": skip})
    assert r.run()["ok"]


def test_expect_result_and_editor_state_poll(tmp_path):
    states = [{"assetsReady": True, "indexReady": False}, {"assetsReady": True, "indexReady": False}, {"assetsReady": True, "indexReady": True}]
    r, *_ = make(tmp_path, [step("go", "launch"),
                            step("e", "editor_invoke", key="k", expect={"result": {"dispatched": True}, "editor.state": {"indexReady": True}, "within_s": 10})],
                 {"editor_state": [{"assetsReady": True}] + states, "editor_invoke": {"dispatched": True, "key": "k"}})
    assert r.run()["ok"]


def test_expect_not_met_times_out_and_logs_observed(tmp_path):
    r, _, _, log, run_dir = make(tmp_path, [step("go", "launch"), step("e", "editor_invoke", key="k", expect={"result": {"dispatched": False}, "within_s": 2})],
                                 {"editor_invoke": {"dispatched": True}})
    out = r.run()
    assert not out["ok"] and out["steps"][1]["detail"] == "expectation not met"
    log.close()
    expect = [json.loads(line) for line in (run_dir / "events.jsonl").read_text().splitlines() if '"kind": "expect"' in line][0]
    assert expect["ok"] is False and expect["detail"]["observed"]["result"] == {"dispatched": True}


def test_unimplemented_kind_fails_the_step(tmp_path):
    out = make(tmp_path, [step("c", "scene_set", property="x")])[0].run()
    assert not out["ok"] and "not implemented" in out["steps"][0]["detail"]


def test_missing_launch_fails_clearly(tmp_path):
    out = make(tmp_path, [step("n", "shot")])[0].run()
    assert not out["ok"] and "launch step first" in out["steps"][0]["detail"]


def test_variables_are_substituted_but_event_keeps_the_name(tmp_path):
    r, client, _, log, run_dir = make(tmp_path, [step("go", "launch"), step("e", "editor_invoke", key="$secret_key")], {"editor_invoke": {"dispatched": True}})
    r.variables = {"secret_key": "hunter2"}
    assert r.run()["ok"]
    assert ("editor_invoke", {"key": "hunter2", "query": ""}) in client.calls
    log.close()
    assert "hunter2" not in (run_dir / "events.jsonl").read_text()


# --- watchdogs --------------------------------------------------------------------------------------------------

def test_step_timeout_fails_the_step(tmp_path):
    running = {"state": "running", "id": "x"}
    r, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x", )], {"tour_segment": running}, timeouts={"step_s": 5})
    r.scenario.steps[1]["timeout_s"] = 3
    out = r.run()
    assert not out["ok"] and "did not finish" in out["steps"][1]["detail"]


def test_run_timeout_stops_run_and_kills_program(tmp_path):
    clock = Clock()
    r, _, session, *_ = make(tmp_path, [step("go", "launch"), step("w", "wait", seconds=4), step("w2", "wait", seconds=4)],
                             clock=clock, timeouts={"step_s": 30, "run_s": 5})
    out = r.run()
    assert out["exit_kind"] == "timeout" and "run timeout" in out["aborted"] and session.stopped


def test_mcp_silence_is_a_hang_and_kills_the_program(tmp_path):
    r, client, session, log, run_dir = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x"), step("after", "note", text="n")],
                                            {"tour_segment": McpTimeout("no MCP reply before the deadline")})
    out = r.run()
    assert out["exit_kind"] == "hang" and not out["ok"] and session.stopped and [x["id"] for x in out["steps"]] == ["go", "t"] and out["steps"][1]["ok"] is False
    log.close()
    kinds = [json.loads(line)["kind"] for line in (run_dir / "events.jsonl").read_text().splitlines()]
    assert "hang" in kinds and kinds[-1] == "run_end"


def test_program_exit_mid_run_aborts(tmp_path):
    session = FakeSession()
    r, *_ = make(tmp_path, [step("go", "launch"), step("w", "wait", seconds=3)], session=session)
    orig = r._sleep

    def die(s):
        session._alive = False
        orig(s)
    r._sleep = die
    out = r.run()
    assert not out["ok"] and "exited" in out["aborted"]


def test_mcp_error_fails_step_not_run(tmp_path):
    r, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x")], {"tour_segment": McpError("boom")})
    out = r.run()
    assert out["steps"][1]["ok"] is False and out["exit_kind"] == "failed"


# --- the record -------------------------------------------------------------------------------------------------

MANIFEST = {"run_id": "20261005_120000_a.b_ai", "project": "guo", "scenario": "a.b", "driver": "ai", "commit": "abc1234", "started": "2026-10-05T12:00:00Z",
            "ended": "2026-10-05T12:01:00Z", "ok": False, "build": "debug",
            "steps": [{"id": "s1", "kind": "note", "ok": True, "skipped": False, "dur_ms": 10, "detail": ""},
                      {"id": "s2", "kind": "tour_segment", "ok": False, "skipped": False, "dur_ms": 20, "detail": "no tab"}]}


def test_registry_rows(tmp_path):
    db = tmp_path / "shared" / "runs.db"
    publish.register_run(db, MANIFEST)
    publish.register_run(db, MANIFEST)       # a re-register replaces, never duplicates
    con = sqlite3.connect(db)
    assert con.execute("SELECT scenario, ok, steps_total, steps_failed FROM runs").fetchall() == [("a.b", 0, 2, 1)]
    assert con.execute("SELECT step, ok FROM run_steps ORDER BY seq").fetchall() == [("s1", 1), ("s2", 0)]


def test_summary_lists_failed_steps():
    text = publish.render_summary({**MANIFEST, "aborted": "step s2 failed"}, "A title")
    assert "**Status:** FAIL" in text and "### s2" in text and "no tab" in text and "stopped early" in text


def test_copy_shared_redacts_and_copies_stills(tmp_path):
    run = tmp_path / "run"
    (run / "shots").mkdir(parents=True)
    (run / "shots" / "a.png").write_bytes(b"png")
    (run / "events.jsonl").write_text(json.dumps({"x": WIN + "Users\\someone\\proj acct_gm1"}) + "\n", encoding="utf-8")
    (run / "run.json").write_text("{}", encoding="utf-8")
    target = publish.copy_shared(run, tmp_path / "shared", "rid", ["acct_gm1"])
    text = (target / "events.jsonl").read_text()
    assert "someone" not in text and "acct_gm1" not in text and (target / "shots" / "a.png").is_file()


def test_redact_rules():
    ip = ".".join(["10", "1", "2", "3"])
    assert redact("opened " + WIN + "work\\x.png at " + ip) == "opened <local path> at host"
    assert redact("/" + "home/someone/x") == "~/x"


# --- review fixes (PR 17) ---------------------------------------------------------------------------------------

def test_preapproved_excludes_editor_invoke():
    import session
    assert "editor_invoke" not in session.PREAPPROVED.split(",")


def test_registry_gets_redacted_summary_and_step_detail(tmp_path):
    db = tmp_path / "runs.db"
    leaky = {**MANIFEST, "summary": "opened " + WIN + "work/x.png acct_gm1",
             "steps": [{**MANIFEST["steps"][0], "detail": "failed at " + WIN + "work/y.png for acct_gm1"}]}
    publish.register_run(db, leaky, ["acct_gm1"])
    con = sqlite3.connect(db)
    text = " ".join(str(v) for row in con.execute("SELECT summary FROM runs UNION ALL SELECT detail FROM run_steps") for v in row)
    assert "work" not in text and "acct_gm1" not in text and "<local path>" in text


def test_expect_log_and_failure_detail_keep_the_name_not_the_value(tmp_path):
    r, _, _, log, run_dir = make(tmp_path, [step("go", "launch"), step("n", "note", text="x", expect={"result": {"note": "$secret"}})])
    r.variables = {"secret": "hunter2"}
    out = r.run()
    assert not out["ok"]
    log.close()
    text = (run_dir / "events.jsonl").read_text()
    assert "hunter2" not in text and "$secret" in text


def test_tour_segment_repoll_sends_no_id(tmp_path):
    running = {"state": "running", "id": "x"}
    done = {"state": "done", "id": "x", "ok": True, "frames": [], "passed": [], "failures": []}
    r, client, *_ = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x")], {"tour_segment": [running, running, done]})
    assert r.run()["ok"]
    sent = [a for t, a in client.calls if t == "tour_segment"]
    assert sent[0]["id"] == "x" and all("id" not in a for a in sent[1:]) and len(sent) == 3


def test_oserror_and_valueerror_abort_with_a_manifest(tmp_path):
    for exc in (ConnectionResetError("reset"), ValueError("bad json")):
        r, _, session, log, run_dir = make(tmp_path / type(exc).__name__, [step("go", "launch"), step("t", "tour_segment", id="x"), step("after", "note", text="n")],
                                           {"tour_segment": exc})
        out = r.run()
        assert out["exit_kind"] == "error" and not out["ok"] and session.stopped
        assert out["aborted"] and type(exc).__name__ in out["aborted"]
        assert [x["id"] for x in out["steps"]] == ["go", "t"]


def test_aborted_step_gets_step_end_and_a_failed_row(tmp_path):
    r, _, _, log, run_dir = make(tmp_path, [step("go", "launch"), step("t", "tour_segment", id="x")], {"tour_segment": McpTimeout("silent")})
    out = r.run()
    row = out["steps"][-1]
    assert row["id"] == "t" and row["ok"] is False and "hang" in row["detail"]
    log.close()
    ends = [json.loads(l) for l in (run_dir / "events.jsonl").read_text().splitlines() if json.loads(l)["kind"] == "step_end"]
    assert [e["step"] for e in ends] == ["go", "t"] and ends[-1]["ok"] is False


def test_run_timeout_inside_a_step_records_that_step(tmp_path):
    clock = Clock()
    r, *_ = make(tmp_path, [step("go", "launch"), step("w", "wait", seconds=9)], clock=clock, timeouts={"step_s": 30, "run_s": 5})
    out = r.run()
    assert out["exit_kind"] == "timeout" and [x["id"] for x in out["steps"]] == ["go", "w"] and out["steps"][1]["ok"] is False


def test_redact_unc_and_own_names(tmp_path):
    import getpass
    from pathlib import Path as P
    assert redact("see " + chr(92) * 2 + "srv" + chr(92) + "share" + chr(92) + "a.png now") == "see <network path> now"
    from redact import load_deny
    deny = load_deny(tmp_path)
    user = getpass.getuser()
    if len(user) >= 3:
        assert user in deny and "<redacted>" in redact("hello " + user + " there", deny)
    assert str(P.home()) in deny


SCHEMA_DIR = Path(__file__).resolve().parents[1] / "scenarios" / "schema"


def test_committed_scenarios_match_the_schema_and_the_runner():
    jsonschema = pytest.importorskip("jsonschema")
    schema = json.loads((SCHEMA_DIR / "scenario.schema.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator.check_schema(schema)
    files = sorted((SCHEMA_DIR.parent).rglob("*.scenario.json"))
    assert files
    for f in files:
        data = json.loads(f.read_text(encoding="utf-8"))
        assert sc.validate(data) == [], f.name
        assert [e.message for e in jsonschema.Draft202012Validator(schema).iter_errors(data)] == [], f.name


def test_schema_kinds_match_the_runner():
    schema = json.loads((SCHEMA_DIR / "scenario.schema.json").read_text(encoding="utf-8"))
    assert set(schema["$defs"]["do"]["properties"]["kind"]["enum"]) == sc.KINDS
    events_schema = json.loads((SCHEMA_DIR / "event.schema.json").read_text(encoding="utf-8"))
    assert set(events_schema["properties"]["kind"]["enum"]) == ev.KINDS


def test_runner_refuses_what_the_schema_refuses():
    base = {"id": "editor.test.typo", "surface": "editor"}
    assert sc.validate({**base, "steps": [{"id": "a", "do": {"kind": "note", "text": "t"}}]}) == []
    cases = [
        ({**base, "colour": 1, "steps": [{"id": "a", "do": {"kind": "wait"}}]}, "unknown field 'colour'"),
        ({**base, "steps": [{"id": "a", "do": {"kind": "note", "txet": "t"}}]}, "note takes no 'txet'"),
        ({**base, "steps": [{"id": "a", "do": {"kind": "tour_segment"}}]}, "tour_segment needs 'id'"),
        ({**base, "steps": [{"id": "a", "do": {"kind": "wait"}, "shoot": True}]}, "unknown field 'shoot'"),
        ({**base, "steps": [{"id": "a", "do": {"kind": "wait"}, "expect": {"ui.exsts": "X"}}]}, "unknown expectation 'ui.exsts'"),
        ({**base, "steps": [{"id": "a", "do": {"kind": "ui.click", "control": "LoginGump", "buton": "Left"}}]}, "ui.click takes no 'buton'"),
    ]
    for data, wanted in cases:
        assert any(wanted in p for p in sc.validate(data)), (wanted, sc.validate(data))


# --- prune and list --scenario ---------------------------------------------------------------------------------

import prune as prune_mod  # noqa: E402


def make_run(root, scenario, n, project="guo", manifest=True, video=False):
    run_id = f"20261001_0000{n:02d}_{scenario}_ai"
    d = root / run_id
    d.mkdir(parents=True)
    if manifest:
        (d / "run.json").write_text(json.dumps({"run_id": run_id, "project": project, "scenario": scenario,
                                                "started": f"2026-10-01T00:00:{n:02d}Z"}), encoding="utf-8")
    if video:
        (d / "run.mp4").write_bytes(b"x")
    return d


def names(root):
    return sorted(p.name for p in root.iterdir())


def test_prune_keeps_n_newest_per_scenario(tmp_path):
    local = tmp_path / "runs"
    for n in range(5):
        make_run(local, "a.one", n)
        make_run(local, "b.two", n)
    res = prune_mod.prune(local, None, keep_local=2, keep_shared=200)
    kept = names(local)
    assert len(kept) == 4
    assert all(any(f"_0000{n:02d}_" in k for k in kept) for n in (3, 4))
    assert len(res["local a.one"].deleted) == 3 and len(res["local b.two"].deleted) == 3


def test_prune_dry_run_deletes_nothing(tmp_path):
    local = tmp_path / "runs"
    for n in range(4):
        make_run(local, "a.one", n)
    res = prune_mod.prune(local, None, keep_local=1, keep_shared=1, dry_run=True)
    assert len(names(local)) == 4
    assert len(res["local a.one"].deleted) == 3


def test_prune_leaves_folders_without_a_matching_run_json_and_video_masters(tmp_path):
    local = tmp_path / "runs"
    stray = make_run(local, "a.one", 0, manifest=False)
    (local / "scratch").mkdir()
    liar = local / "20261001_000009_a.one_ai"
    liar.mkdir()
    (liar / "run.json").write_text(json.dumps({"run_id": "something_else", "scenario": "a.one"}), encoding="utf-8")
    master = make_run(local, "a.one", 1, video=True)
    for n in (2, 3, 4):
        make_run(local, "a.one", n)
    prune_mod.prune(local, None, keep_local=1, keep_shared=1)
    left = names(local)
    assert stray.name in left and "scratch" in left and liar.name in left and master.name in left
    assert left == sorted([stray.name, "scratch", liar.name, master.name, "20261001_000004_a.one_ai"])


def test_prune_shared_is_per_project_and_stays_out_of_the_registry(tmp_path):
    local, shared, db = tmp_path / "runs", tmp_path / "shared", tmp_path / "runs.db"
    for n in range(3):
        make_run(shared, "a.one", n, project="guo")
        make_run(shared, "x.other", n, project="mgs5")
    con = registry.connect(db)
    con.execute("INSERT INTO runs (run_id, project, scenario, driver, started) VALUES ('r1','guo','a.one','ai','t')")
    con.commit()
    con.close()
    res = prune_mod.prune(local, shared, keep_local=30, keep_shared=1)
    assert len(res["shared guo"].deleted) == 2 and len(res["shared mgs5"].deleted) == 2
    assert len(names(shared)) == 2
    con = sqlite3.connect(db)
    assert con.execute("SELECT count(*) FROM runs").fetchone()[0] == 1
    con.close()


def test_list_scenario_filters_the_registry_by_id_prefix(tmp_path):
    con = registry.connect(tmp_path / "runs.db")
    for i, scen_id in enumerate(("editor.tabs.sweep", "editor.anim.roundtrip", "client.login.basic", "editor_x.y")):
        con.execute("INSERT INTO runs (run_id, project, scenario, driver, started, ok, steps_total, steps_failed) "
                    "VALUES (?,?,?,?,?,1,3,0)", (f"r{i}", "guo", scen_id, "ai", f"2026-10-0{i + 1}"))
    con.commit()
    assert [r[0] for r in registry.recent(con, "guo", "editor.")] == ["r1", "r0"]
    assert len(registry.recent(con, "guo")) == 4
    assert registry.recent(con, "guo", "nope") == []
    con.close()


# --- main(argv): the command line itself -------------------------------------------------------------------------

import shutil  # noqa: E402
from types import SimpleNamespace  # noqa: E402

import run as run_mod  # noqa: E402

REPO = Path(__file__).resolve().parents[2]


@pytest.fixture
def cli_root(tmp_path, monkeypatch):
    """A temp project root holding copies of the committed scenarios; no real shared folder or registry."""
    root = tmp_path / "proj"
    shutil.copytree(REPO / "tools" / "scenarios", root / "tools" / "scenarios")
    other = json.loads((root / "tools" / "scenarios" / "editor" / "smoke_layout.scenario.json").read_text(encoding="utf-8"))
    other["id"] = "client.smoke.other"
    (root / "tools" / "scenarios" / "client").mkdir()
    (root / "tools" / "scenarios" / "client" / "other.scenario.json").write_text(json.dumps(other), encoding="utf-8")
    import guo
    monkeypatch.setattr(guo, "load_config", lambda: SimpleNamespace(root=root))
    for var in ("GUO_RUNS_DB", "GUO_RUNS_SHARED_DIR"):
        monkeypatch.setenv(var, "")
    return root


def listed_ids(out):
    return {line.split()[0] for line in out.splitlines() if "steps" in line}


@pytest.mark.parametrize("argv", [["list", "--scenario", "editor"], ["list", "--scenario=editor"],
                                  ["--scenario", "editor", "list"]])
def test_main_list_scenario_spellings(cli_root, capsys, argv):
    assert run_mod.main(argv) == 0
    ids = listed_ids(capsys.readouterr().out)
    assert ids and all(i.startswith("editor") for i in ids)


def test_main_plain_list_shows_every_scenario(cli_root, capsys):
    assert run_mod.main(["list"]) == 0
    assert listed_ids(capsys.readouterr().out) == {"editor.smoke.layout", "client.smoke.other"}


def test_main_scenario_run_still_sees_one_name(cli_root, capsys):
    assert run_mod.main(["editor.nope", "--scenario", "x"]) == 2
    assert "give one scenario" not in capsys.readouterr().err


def test_main_prune_dry_run_deletes_nothing(cli_root, capsys):
    runs = cli_root / "build" / "runs"
    for i in range(3):
        make_run(runs, "editor.a", i)
    assert run_mod.main(["prune", "--keep-local", "1", "--dry-run"]) == 0
    assert "would delete 2" in capsys.readouterr().out
    assert len(names(runs)) == 3


# --- post: the Discord card ------------------------------------------------------------------------------------

import card as card_mod  # noqa: E402

TESTDATA = Path(__file__).resolve().parent / "testdata"


def manifest_for(run_id, scenario, steps, **extra):
    base = {"run_id": run_id, "project": "guo", "scenario": scenario, "title": "Editor tabs sweep", "driver": "ai",
            "commit": "abc1234", "ok": all(s["ok"] is not False for s in steps), "aborted": None, "steps": steps}
    base.update(extra)
    return base


def put_run(root, manifest, stills=()):
    d = root / manifest["run_id"]
    d.mkdir(parents=True)
    (d / "run.json").write_text(json.dumps(manifest), encoding="utf-8")
    if stills:
        (d / "shots").mkdir()
        for name in stills:
            (d / "shots" / name).write_bytes(b"png")
    return d


def golden(name, card):
    path = TESTDATA / name
    assert json.loads(path.read_text(encoding="utf-8")) == card


def ok_step(sid, ms=1000):
    return {"id": sid, "ok": True, "dur_ms": ms, "detail": ""}


def test_card_for_a_passing_run_matches_the_golden(tmp_path):
    m = manifest_for("20261001_000000_editor.tabs.sweep_ai", "editor.tabs.sweep", [ok_step("launch", 61000), ok_step("art", 2000)])
    d = put_run(tmp_path, m, ["art.png", "store.png"])
    card = card_mod.build_card(m, d, [])
    golden("card_pass.json", card)
    assert card["status"] == "PASS" and card["failed_steps"] == []


def test_card_for_a_failing_run_matches_the_golden(tmp_path):
    steps = [ok_step("launch"), {"id": "store", "ok": False, "dur_ms": 5000, "detail": "timeout"}, ok_step("pick")]
    m = manifest_for("20261001_000100_editor.tabs.sweep_ai", "editor.tabs.sweep", steps, aborted="step store failed")
    d = put_run(tmp_path, m)
    card = card_mod.build_card(m, d, [])
    golden("card_fail.json", card)
    assert card["failed_steps"] == ["store"]


def test_a_forty_step_failing_run_fits_the_body_limit_and_says_more(tmp_path):
    steps = [{"id": f"segment_with_a_long_name_{i:02d}", "ok": False, "dur_ms": 1000, "detail": "x"} for i in range(40)]
    m = manifest_for("20261001_000200_editor.big_ai", "editor.big", steps, title="T" * 200, aborted="y" * 500)
    d = put_run(tmp_path, m, [f"shot_{i}.png" for i in range(40)])
    card = card_mod.build_card(m, d, [])
    assert len(card["text"]) <= card_mod.BODY_LIMIT
    assert "more" in card["text"] and "+" in card["text"]
    assert len(card["failed_steps"]) == 40


def test_card_text_is_redacted(tmp_path):
    steps = [{"id": "s1", "ok": False, "dur_ms": 1, "detail": ""}]
    m = manifest_for("20261001_000300_editor.r_ai", "editor.r", steps, title=r"open D:\Work\notes.txt", aborted="at secretword")
    d = put_run(tmp_path, m)
    card = card_mod.build_card(m, d, ["secretword"])
    blob = json.dumps(card)
    assert "Work" not in blob and "secretword" not in blob and "D:\\" not in blob


def test_find_run_prefers_local_then_shared_and_refuses_unknown(tmp_path):
    local, shared = tmp_path / "root" / "build" / "runs", tmp_path / "shared"
    m = manifest_for("20261001_000400_a_ai", "a", [ok_step("x")])
    put_run(shared, m)
    assert card_mod.find_run(tmp_path / "root", str(shared), m["run_id"]) == shared / m["run_id"]
    put_run(local, m)
    assert card_mod.find_run(tmp_path / "root", str(shared), m["run_id"]) == local / m["run_id"]
    assert card_mod.find_run(tmp_path / "root", str(shared), "nope") is None
    assert card_mod.find_run(tmp_path / "root", str(shared), "../x") is None


def test_main_post_writes_card_json_and_prints_it(cli_root, capsys):
    m = manifest_for("20261001_000500_editor.p_ai", "editor.p", [ok_step("a")])
    d = put_run(cli_root / "build" / "runs", m)
    assert run_mod.main(["post", m["run_id"]]) == 0
    printed = json.loads(capsys.readouterr().out)
    assert printed == json.loads((d / "card.json").read_text(encoding="utf-8"))
    assert printed["status"] == "PASS"


def test_main_post_unknown_run_id_exits_2(cli_root, capsys):
    assert run_mod.main(["post", "20260101_000000_nothing_ai"]) == 2
    assert "unknown run id" in capsys.readouterr().err


def test_card_module_has_no_network_or_token_use():
    src = (Path(__file__).resolve().parent / "card.py").read_text(encoding="utf-8")
    code = src.split('"""', 2)[2]
    imports = [ln for ln in code.splitlines() if ln.startswith(("import ", "from "))]
    assert sorted(imports) == ["from __future__ import annotations", "from pathlib import Path", "from redact import redact", "import json"]
    assert "environ" not in code and "getenv" not in code and "read_setting" not in code
