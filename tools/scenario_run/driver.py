"""The AI driver: performs each step of a scenario, then polls its expectation until it holds or its time is up.

Three watchdogs, as in the plan: a step timeout (the step fails, the run continues or aborts per `on_fail`),
a run timeout (the run stops, the program is killed), and a process watchdog (the program exited, or answered
nothing within the MCP deadline: that is a hang, the program is killed and the run ends non-zero).
"""

from __future__ import annotations

import base64
import json
import time
from pathlib import Path

import uiquery
from events import EventLog, Stopwatch, utc_now
from mcp_client import McpError, McpTimeout
from scenario import IMPLEMENTED, Scenario, ScenarioError, subset, substitute

# Kinds that legitimately take longer than the scenario's default step timeout.
KIND_TIMEOUTS = {"launch": 300, "tour_segment": 180}
MCP_DEADLINE_S = 45          # no reply this long from the program: a hang


# The game MCP's tool names (tools/guo_mcp, docs/data_formats.md section 29); the scenarios never name them.
GAME_TOOLS = {"ui": "guo_ui", "input": "guo_input", "state": "guo_state", "shot": "guo_screenshot", "quit": "guo_quit"}


class RunAborted(Exception):
    """The run cannot go on (hang, run timeout, program gone). `reason` goes into the manifest."""

    def __init__(self, reason: str, kind: str = "error"):
        super().__init__(reason)
        self.reason = reason
        self.kind = kind


class StepFailed(Exception):
    def __init__(self, why: str, detail: dict | None = None):
        super().__init__(why)
        self.why = why
        self.detail = detail or {}


class Runner:
    """Runs one scenario. `session` starts and stops the program; `connect(session)` returns an MCP client."""

    def __init__(self, scenario: Scenario, run_dir: Path, events: EventLog, *, repo_root: Path, session=None, connect=None,
                 variables: dict[str, str] | None = None, clock=time.monotonic, sleep=time.sleep):
        self.scenario = scenario
        self.run_dir = run_dir
        self.events = events
        self.repo_root = repo_root
        self.session = session
        self._connect = connect
        self.client = None
        self.variables = variables or {}
        self._clock = clock
        self._sleep = sleep
        self.started_utc = utc_now()
        self.steps: list[dict] = []
        self.artifacts: list[str] = []
        self.aborted: str | None = None
        self.exit_kind = "ok"            # ok | failed | hang | timeout
        self._run_deadline = 0.0
        self._last: dict = {}            # the last action's JSON result, what `expect.result` compares against

    # -- the run -----------------------------------------------------------------------------------------------

    def run(self) -> dict:
        sc = self.scenario
        total = Stopwatch()
        self._run_deadline = self._clock() + float(sc.timeouts["run_s"])
        self.events.emit("run_start", detail={"scenario": sc.id, "title": sc.title, "surface": sc.surface,
                                              "steps": len(sc.steps), "run_s": sc.timeouts["run_s"]})
        try:
            for step in sc.steps:
                self._check_run_deadline()
                result = self._run_step(step)
                self.steps.append(result)
                if result["ok"] is False and step.get("on_fail", "abort") == "abort":
                    self.aborted = f"step {step['id']} failed"
                    break
        except RunAborted as ex:
            self.aborted = ex.reason
            self.exit_kind = ex.kind
            self.events.emit(ex.kind if ex.kind in ("hang",) else "error", detail={"reason": ex.reason})
        finally:
            self._stop_program()
        failed = [s for s in self.steps if s["ok"] is False]
        ok = not failed and self.aborted is None
        if self.exit_kind == "ok" and not ok:
            self.exit_kind = "failed"
        self.events.emit("run_end", ok=ok, dur_ms=total.ms(), detail={
            "steps_run": len(self.steps), "steps_failed": len(failed), "aborted": self.aborted})
        return {"ok": ok, "aborted": self.aborted, "exit_kind": self.exit_kind, "steps": self.steps,
                "started": self.started_utc, "ended": utc_now(), "artifacts": self.artifacts}

    def _stop_program(self) -> None:
        if self.client is not None:
            if self.scenario.surface == "client" and getattr(self.session, "record", False) and self._program_up():
                try:                      # a MovieWriter file is only complete when the engine exits by itself
                    self.client.call(GAME_TOOLS["quit"], {}, timeout=5)
                except Exception:
                    pass
                self.session.wait_exit(30)
            self.client.close()
            self.client = None
        if self.session is not None:
            self.session.stop()

    def _check_run_deadline(self) -> None:
        if self._clock() > self._run_deadline:
            raise RunAborted(f"run timeout ({self.scenario.timeouts['run_s']} s)", "timeout")

    def _check_run_deadline_hit(self) -> None:
        raise RunAborted(f"run timeout ({self.scenario.timeouts['run_s']} s)", "timeout")

    def _program_up(self) -> bool:
        return self.session is not None and self.session.alive()

    def _check_program(self) -> None:
        if self.session is not None and self.client is not None and not self.session.alive():
            raise RunAborted("the program exited during the run", "error")
        stalled = getattr(self.session, "stalled", None)
        if self.client is not None and stalled is not None and stalled():
            raise RunAborted("no frame was written for 20 s: the engine is wedged", "hang")

    def _mark_frame(self) -> None:
        """On the client the event `frame` is the engine's frame index (the movie frame when recording)."""
        if self.scenario.surface != "client" or self.client is None:
            return
        try:
            is_error, text = self.client.call(GAME_TOOLS["state"], {}, timeout=5)
            if not is_error:
                self.events.frame = int(json.loads(text)["frame"])
        except Exception:
            pass

    # -- one step ----------------------------------------------------------------------------------------------

    def _run_step(self, step: dict) -> dict:
        sid = step["id"]
        kind = step["do"]["kind"]
        watch = Stopwatch()
        self._mark_frame()
        self.events.emit("step_start", sid, detail={"kind": kind, "say": step.get("say")})
        if kind not in IMPLEMENTED:
            return self._finish(step, watch, False, f"step kind '{kind}' is not implemented in this runner yet", {})
        timeout = float(step.get("timeout_s") or KIND_TIMEOUTS.get(kind) or self.scenario.timeouts["step_s"])
        deadline = min(self._clock() + timeout, self._run_deadline)
        try:
            self._check_program()
            return self._step_body(step, sid, kind, watch, deadline)
        except StepFailed as ex:
            return self._finish(step, watch, False, ex.why, ex.detail)
        except ScenarioError as ex:
            return self._finish(step, watch, False, str(ex), {})
        except RunAborted as ex:
            self._abort_step(step, watch, ex.reason)
            raise
        except McpTimeout as ex:
            self._abort_step(step, watch, f"hang in step {sid}: {ex}")
            raise RunAborted(f"hang in step {sid}: {ex}", "hang") from ex
        except McpError as ex:
            try:
                self._check_program()
            except RunAborted as gone:
                self._abort_step(step, watch, gone.reason)
                raise
            return self._finish(step, watch, False, str(ex), {})
        except (OSError, ValueError) as ex:      # a socket or a reply that is not JSON: the run ends, but is still recorded
            reason = f"step {sid}: {type(ex).__name__}: {ex}"
            self._abort_step(step, watch, reason)
            raise RunAborted(reason, "error") from ex

    def _step_body(self, step: dict, sid: str, kind: str, watch: Stopwatch, deadline: float) -> dict:
        """Performs the step's `do`, polls its `expect`, takes its still; returns the step's row. The human driver overrides this."""
        do = substitute(step["do"], self.variables)
        expect = substitute(step.get("expect", {}), self.variables)
        shown_expect = step.get("expect", {})       # as written: the log never holds what a $name stands for
        # The step as written (with $names, never the secrets behind them) is what the event log records.
        self.events.emit("action", sid, detail={"do": step["do"]})
        detail = getattr(self, "_do_" + kind.replace(".", "_"))(sid, do, deadline)
        self._last = detail if isinstance(detail, dict) else {}
        self._expect(sid, expect, deadline, shown_expect)
        if step.get("shot"):
            self._screenshot(sid)
        return self._finish(step, watch, True, None, detail)

    def _abort_step(self, step: dict, watch: Stopwatch, reason: str) -> None:
        """The step that was running when the run ended gets its step_end and its row, as failed."""
        self._kill()
        self.steps.append(self._finish(step, watch, False, reason, {}))

    def _finish(self, step: dict, watch: Stopwatch, ok: bool, why: str | None, detail: dict) -> dict:
        sid = step["id"]
        shown = dict(detail) if isinstance(detail, dict) else {}
        self._mark_frame()
        if why:
            shown["failure"] = why
        self.events.emit("step_end", sid, ok=ok, dur_ms=watch.ms(), detail=shown)
        return {"id": sid, "kind": step["do"]["kind"], "ok": ok, "skipped": False, "dur_ms": watch.ms(),
                "detail": why or ""}

    def _kill(self) -> None:
        if self.session is not None:
            self.session.stop()

    # -- expectations ------------------------------------------------------------------------------------------

    def _expect(self, sid: str, expect: dict, deadline: float, shown: dict | None = None) -> None:
        conditions = {k: v for k, v in expect.items() if k != "within_s"}
        if not conditions:
            return
        end = min(self._clock() + float(expect.get("within_s", 0)), deadline)
        while True:
            ok, observed = self._conditions_hold(conditions)
            if ok or self._clock() >= end:
                break
            self._check_program()
            self._sleep(0.5)
        written = {k: v for k, v in (shown if shown is not None else expect).items() if k != "within_s"}
        self.events.emit("expect", sid, ok=ok, detail={"expect": written, "observed": observed})
        if not ok:
            raise StepFailed("expectation not met", {"expect": written, "observed": observed})

    def _conditions_hold(self, conditions: dict) -> tuple[bool, dict]:
        """One look at every condition: (all hold, what each was observed to be). The AI and human drivers poll with this."""
        observed: dict = {}
        ok = True
        for name, wanted in conditions.items():
            good, seen = self._evaluate(name, wanted)
            observed[name] = seen
            ok = ok and good
        return ok, observed

    def _evaluate(self, name: str, wanted) -> tuple[bool, object]:
        if name == "result":
            return subset(wanted, self._last), self._last
        if name == "editor.state":
            state = self._tool_json("editor_state", {})
            return subset(wanted, state), {k: state.get(k) for k in wanted}
        if name == "ui.exists":
            hits = uiquery.find_all(self._snapshot(), wanted)
            return bool(hits), len(hits)
        if name == "ui.absent":
            hits = uiquery.find_all(self._snapshot(), wanted)
            return not hits, len(hits)
        if name in ("ui.text", "ui.count"):
            return self._expect_ui(name, wanted)
        if name == "world.position":
            return self._expect_position(wanted)
        if name == "scene":
            scene = self._tool_json(GAME_TOOLS["state"], {}).get("scene")
            return scene == wanted, scene
        if name == "log.contains":
            return self._expect_log(wanted)
        if name == "file.exists":
            path = self._under_repo_build(wanted)
            return path.exists(), path.exists()
        raise StepFailed(f"unknown expectation '{name}'")

    def _expect_ui(self, name: str, wanted) -> tuple[bool, object]:
        if not isinstance(wanted, dict) or "control" not in wanted:
            raise StepFailed(f'{name} needs {{"control": ..., "equals" | "contains" | "at_least": ...}}')
        hits = uiquery.find_all(self._snapshot(), wanted["control"])
        if name == "ui.count":
            if "equals" in wanted:
                return len(hits) == int(wanted["equals"]), len(hits)
            return len(hits) >= int(wanted.get("at_least", 1)), len(hits)
        index = int(uiquery.normalize(wanted["control"]).get("index", 0))
        text = hits[index].get("text") if 0 <= index < len(hits) else None
        if text is None:
            return False, None
        if "equals" in wanted:
            return text == wanted["equals"], text
        return str(wanted.get("contains", "")) in text, text

    def _expect_position(self, wanted: dict) -> tuple[bool, object]:
        player = self._tool_json(GAME_TOOLS["state"], {}).get("player")
        if not player:
            return False, None
        tol = int(wanted.get("tolerance", 0))
        ok = all(abs(int(player[a]) - int(wanted[a])) <= tol for a in ("x", "y", "z") if a in wanted)
        if "map" in wanted:
            ok = ok and int(player["map"]) == int(wanted["map"])
        return ok, player

    def _expect_log(self, wanted) -> tuple[bool, object]:
        path = getattr(self.session, "log_path", None)
        if path is None or not Path(path).is_file():
            return False, "no log"
        found = str(wanted) in Path(path).read_text(encoding="utf-8", errors="replace")
        return found, found

    def _under_repo_build(self, value: str) -> Path:
        base = (self.repo_root / "build").resolve()
        full = (self.repo_root / value).resolve() if not Path(value).is_absolute() else Path(value).resolve()
        if base != full and base not in full.parents:
            raise StepFailed("file.exists must name a path under build/")
        return full

    # -- calling the program -----------------------------------------------------------------------------------

    def _need_client(self):
        if self.client is None:
            raise StepFailed("no program is running: the scenario needs a launch step first")
        return self.client

    def _tool(self, tool: str, arguments: dict) -> tuple[bool, str]:
        client = self._need_client()
        self._check_program()
        return client.call(tool, arguments, timeout=MCP_DEADLINE_S)

    def _tool_json(self, tool: str, arguments: dict) -> dict:
        import json
        is_error, text = self._tool(tool, arguments)
        if is_error:
            raise StepFailed(f"{tool} failed: {text[:300]}")
        try:
            return json.loads(text)
        except ValueError as ex:
            raise StepFailed(f"{tool} returned something that is not JSON: {text[:200]}") from ex

    # -- the step kinds ----------------------------------------------------------------------------------------

    def _do_launch(self, sid: str, do: dict, deadline: float) -> dict:
        surface = self.scenario.surface
        if surface not in ("editor", "client"):
            raise StepFailed(f"launching a '{surface}' surface is not implemented in this runner yet")
        if self.client is not None:
            raise StepFailed("the program is already running")
        if self.session is None or self._connect is None:
            raise StepFailed(f"this run has no way to start the {surface}")
        if surface == "client":
            return self._launch_client(do, deadline)
        self.session.start()
        if not self.session.wait_listening(max(1.0, deadline - self._clock())):
            raise StepFailed("the editor did not open its MCP port (is AI on in Editor Settings, and the build fresh?)")
        self.client = self._connect(self.session)
        ready = {"assetsReady": True}
        while True:
            state = self._tool_json("editor_state", {})
            if subset(ready, state):
                return {"launched": True, "assetsReady": True}
            if self._clock() >= deadline:
                raise StepFailed("the editor opened but the client data never finished loading", {"state": state})
            self._check_program()
            self._sleep(1.0)

    def _launch_client(self, do: dict, deadline: float) -> dict:
        self.session.extra_args = [str(a) for a in do.get("args", [])]
        self.session.extra_settings = dict(do.get("settings", {}))
        self.session.start()
        if not self.session.wait_listening(max(1.0, deadline - self._clock())):
            raise StepFailed("the client did not open its MCP port (is the build fresh and the UO data found?)")
        self.client = self._connect(self.session)
        while True:
            state = self._tool_json(GAME_TOOLS["state"], {})
            if state.get("scene"):
                return {"launched": True, "scene": state["scene"], "width": state.get("width"), "height": state.get("height")}
            if self._clock() >= deadline:
                raise StepFailed("the client opened but never showed a scene", {"state": state})
            self._check_program()
            self._sleep(1.0)

    def _do_wait(self, sid: str, do: dict, deadline: float) -> dict:
        seconds = float(do.get("seconds", 1))
        end = self._clock() + seconds
        if end > self._run_deadline:
            while self._clock() < self._run_deadline:
                self._check_program()
                self._sleep(min(0.5, max(0.0, self._run_deadline - self._clock())))
            self._check_run_deadline_hit()
        if end > deadline:
            raise StepFailed(f"wait of {seconds} s does not fit the step timeout")
        while self._clock() < end:
            self._check_program()
            self._sleep(min(0.5, max(0.0, end - self._clock())))
        return {"waited_s": seconds}

    def _do_note(self, sid: str, do: dict, deadline: float) -> dict:
        self.events.emit("log", sid, detail={"note": str(do.get("text", ""))})
        return {"note": str(do.get("text", ""))}

    def _screenshot_client(self, sid: str) -> None:
        is_error, content = self.client.call_content(GAME_TOOLS["shot"], {}, timeout=MCP_DEADLINE_S)
        image = next((c for c in content if c.get("type") == "image"), None)
        if is_error or image is None:
            raise StepFailed("guo_screenshot returned no image: " + " ".join(c.get("text", "") for c in content)[:200])
        png = base64.b64decode(image["data"])
        target = self.run_dir / "shots" / f"{sid}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(png)
        self.artifacts.append(f"shots/{sid}.png")
        self._mark_frame()
        is_png = png[:8] == b"\x89PNG\r\n\x1a\n"
        width = int.from_bytes(png[16:20], "big") if is_png else None
        height = int.from_bytes(png[20:24], "big") if is_png else None
        self.events.emit("shot", sid, ok=True, detail={"file": f"shots/{sid}.png", "width": width, "height": height})

    def _screenshot(self, sid: str) -> None:
        if self.client is None:
            return
        if self.scenario.surface == "client":
            self._screenshot_client(sid)
            return
        rel = (self.run_dir / "shots" / f"{sid}.png").resolve().relative_to(self.repo_root.resolve()).as_posix()
        result = self._tool_json("editor_screenshot", {"file": rel})
        self.artifacts.append(f"shots/{sid}.png")
        self.events.frame = (self.events.frame or 0) + 1
        self.events.emit("shot", sid, ok=True, detail={"file": f"shots/{sid}.png", "width": result.get("width"), "height": result.get("height")})

    def _do_shot(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_client()
        self._screenshot(sid)
        return {"shot": f"shots/{sid}.png"}

    def _do_editor_invoke(self, sid: str, do: dict, deadline: float) -> dict:
        approved = getattr(self.session, "preapproved", None)
        if approved is not None and "editor_invoke" not in approved:
            raise StepFailed("editor_invoke is not pre-approved for a scripted editor (an F3 action can do more than a scenario needs), "
                             "so it would wait for a person at the PC to press Approve; use a tour_segment step, or run it by hand")
        args = {"key": do.get("key", ""), "query": do.get("query", "")}
        return self._tool_json("editor_invoke", args)

    def _do_tour_segment(self, sid: str, do: dict, deadline: float) -> dict:
        seg = do.get("id")
        if not seg:
            raise StepFailed("tour_segment needs an id")
        out = (self.run_dir / "tour" / seg).resolve().relative_to(self.repo_root.resolve()).as_posix()
        args = {"id": seg, "out_dir": out}
        while True:
            result = self._tool_json("tour_segment", args)
            if result.get("state") != "running":
                break
            if self._clock() >= deadline:
                raise StepFailed(f"segment {seg} did not finish within the step timeout")
            self._check_program()
            self._sleep(0.5)
            args = {}                             # ask again with no id: the editor keeps the running segment (an id would restart a finished one)
        frames = result.get("frames", [])
        self.events.frame = (self.events.frame or 0) + len(frames)
        run_rel = self.run_dir.resolve().relative_to(self.repo_root.resolve()).as_posix() + "/"
        self.artifacts += [f["file"].removeprefix(run_rel) for f in frames]
        detail = {"segment": seg, "passed": result.get("passed", []), "failures": result.get("failures", []),
                  "skipped": result.get("skipped"), "frames": [f["file"].removeprefix(run_rel) for f in frames]}
        if not result.get("ok") and not (result.get("skipped") and not result.get("failures")):
            raise StepFailed("; ".join(result.get("failures", [])) or f"segment {seg} failed", detail)
        return detail

    # -- the client's ui.* kinds and chat ----------------------------------------------------------------------

    def _need_game(self, kind: str):
        if self.scenario.surface != "client":
            raise StepFailed(f"{kind} drives the client; this scenario's surface is '{self.scenario.surface}'")
        return self._need_client()

    def _snapshot(self) -> dict:
        self._need_game("a ui expectation")
        return self._tool_json(GAME_TOOLS["ui"], {})

    def _input(self, **args) -> None:
        is_error, text = self._tool(GAME_TOOLS["input"], args)
        if is_error:
            raise StepFailed(f"guo_input {args.get('kind')} failed: {text[:200]}")

    def _key(self, key: str, **mods) -> None:
        self._input(kind="key", key=key, pressed=True, **mods)
        self._input(kind="key", key=key, pressed=False, **mods)

    def _wait_control(self, selector, deadline: float, within_s: float) -> dict:
        end = min(self._clock() + within_s, deadline)
        while True:
            control = uiquery.find(self._snapshot(), selector)
            if control is not None and control.get("width", 0) > 0 and control.get("height", 0) > 0:
                return control
            if self._clock() >= end:
                raise StepFailed(f"no control matches {uiquery.describe(selector)}")
            self._check_program()
            self._sleep(0.5)

    def _click(self, control: dict, button: str = "Left", clicks: int = 1) -> tuple[int, int]:
        x, y = uiquery.center(control)
        self._input(kind="motion", x=x, y=y)
        for _ in range(max(1, clicks)):
            self._input(kind="button", x=x, y=y, button=button, pressed=True)
            self._input(kind="button", x=x, y=y, button=button, pressed=False)
        return x, y

    def _do_ui_click(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("ui.click")
        control = self._wait_control(do.get("control"), deadline, float(do.get("within_s", 5)))
        x, y = self._click(control, str(do.get("button", "Left")), int(do.get("clicks", 1)))
        return {"clicked": uiquery.describe(do.get("control")), "x": x, "y": y}

    def _do_ui_fill(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("ui.fill")
        if "text" not in do:
            raise StepFailed("ui.fill needs text")
        control = self._wait_control(do.get("control"), deadline, float(do.get("within_s", 5)))
        self._click(control)
        for _ in range(int(do.get("clear", 0))):          # BackSpace presses, for a field that already holds text
            self._key("BackSpace")
        self._input(kind="text", text=str(do["text"]))
        # The field's value is never read back (the game omits editable values) and the text is not logged.
        return {"filled": uiquery.describe(do.get("control")), "chars": len(str(do["text"]))}

    def _do_ui_key(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("ui.key")
        key = do.get("key")
        if not key:
            raise StepFailed("ui.key needs a key (a Godot key name, e.g. Enter, Escape, F1)")
        mods = {m: True for m in ("shift", "ctrl", "alt") if do.get(m)}
        self._key(str(key), **mods)
        return {"key": key, **mods}

    def _do_chat(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("chat")
        if "text" not in do:
            raise StepFailed("chat needs text")
        self._key("Enter")
        self._input(kind="text", text=str(do["text"]))
        self._key("Enter")
        return {"chat_chars": len(str(do["text"]))}
