"""The AI driver: performs each step of a scenario, then polls its expectation until it holds or its time is up.

Three watchdogs, as in the plan: a step timeout (the step fails, the run continues or aborts per `on_fail`),
a run timeout (the run stops, the program is killed), and a process watchdog (the program exited, or answered
nothing within the MCP deadline: that is a hang, the program is killed and the run ends non-zero).
"""

from __future__ import annotations

import time
from pathlib import Path

from events import EventLog, Stopwatch, utc_now
from mcp_client import McpError, McpTimeout
from scenario import IMPLEMENTED, Scenario, ScenarioError, subset, substitute

# Kinds that legitimately take longer than the scenario's default step timeout.
KIND_TIMEOUTS = {"launch": 300, "tour_segment": 180}
MCP_DEADLINE_S = 45          # no reply this long from the program: a hang


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
            self.client.close()
            self.client = None
        if self.session is not None:
            self.session.stop()

    def _check_run_deadline(self) -> None:
        if self._clock() > self._run_deadline:
            raise RunAborted(f"run timeout ({self.scenario.timeouts['run_s']} s)", "timeout")

    def _check_run_deadline_hit(self) -> None:
        raise RunAborted(f"run timeout ({self.scenario.timeouts['run_s']} s)", "timeout")

    def _check_program(self) -> None:
        if self.session is not None and self.client is not None and not self.session.alive():
            raise RunAborted("the program exited during the run", "error")

    # -- one step ----------------------------------------------------------------------------------------------

    def _run_step(self, step: dict) -> dict:
        sid = step["id"]
        kind = step["do"]["kind"]
        watch = Stopwatch()
        self.events.emit("step_start", sid, detail={"kind": kind, "say": step.get("say")})
        if kind not in IMPLEMENTED:
            return self._finish(step, watch, False, f"step kind '{kind}' is not implemented in this runner yet", {})
        timeout = float(step.get("timeout_s") or KIND_TIMEOUTS.get(kind) or self.scenario.timeouts["step_s"])
        deadline = min(self._clock() + timeout, self._run_deadline)
        try:
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

    def _abort_step(self, step: dict, watch: Stopwatch, reason: str) -> None:
        """The step that was running when the run ended gets its step_end and its row, as failed."""
        self._kill()
        self.steps.append(self._finish(step, watch, False, reason, {}))

    def _finish(self, step: dict, watch: Stopwatch, ok: bool, why: str | None, detail: dict) -> dict:
        sid = step["id"]
        shown = dict(detail) if isinstance(detail, dict) else {}
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
            observed: dict = {}
            ok = True
            for name, wanted in conditions.items():
                good, seen = self._evaluate(name, wanted)
                observed[name] = seen
                ok = ok and good
            if ok or self._clock() >= end:
                break
            self._check_program()
            self._sleep(0.5)
        written = {k: v for k, v in (shown if shown is not None else expect).items() if k != "within_s"}
        self.events.emit("expect", sid, ok=ok, detail={"expect": written, "observed": observed})
        if not ok:
            raise StepFailed("expectation not met", {"expect": written, "observed": observed})

    def _evaluate(self, name: str, wanted) -> tuple[bool, object]:
        if name == "result":
            return subset(wanted, self._last), self._last
        if name == "editor.state":
            state = self._tool_json("editor_state", {})
            return subset(wanted, state), {k: state.get(k) for k in wanted}
        if name == "file.exists":
            path = self._under_repo_build(wanted)
            return path.exists(), path.exists()
        raise StepFailed(f"unknown expectation '{name}'")

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
        if self.scenario.surface != "editor":
            raise StepFailed(f"launching a '{self.scenario.surface}' surface is not implemented in this runner yet")
        if self.client is not None:
            raise StepFailed("the program is already running")
        if self.session is None or self._connect is None:
            raise StepFailed("this run has no way to start the editor")
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

    def _screenshot(self, sid: str) -> None:
        if self.client is None:
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
