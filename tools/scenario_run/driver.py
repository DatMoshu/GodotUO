"""The AI driver: performs each step of a scenario, then polls its expectation until it holds or its time is up.

Three watchdogs, as in the plan: a step timeout (the step fails, the run continues or aborts per `on_fail`),
a run timeout (the run stops, the program is killed), and a process watchdog (the program exited, or answered
nothing within the MCP deadline: that is a hang, the program is killed and the run ends non-zero).
"""

from __future__ import annotations

import base64
import importlib.util
import json
import re
import time
from pathlib import Path

import settings
import uiquery
from events import EventLog, Stopwatch, utc_now
from mcp_client import McpError, McpTimeout
from scenario import IMPLEMENTED, Scenario, ScenarioError, subset, substitute

# Kinds that legitimately take longer than the scenario's default step timeout.
KIND_TIMEOUTS = {"launch": 300, "tour_segment": 180}
MCP_DEADLINE_S = 45          # no reply this long from the program: a hang


# The game MCP's tool names (tools/guo_mcp, docs/data_formats.md section 29); the scenarios never name them.
GAME_TOOLS = {"ui": "guo_ui", "input": "guo_input", "state": "guo_state", "shot": "guo_screenshot", "quit": "guo_quit",
              "net_drop": "guo_net_drop"}


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
        self._diff: dict = {}            # the last render_diff's counts, what `expect.render_diff` compares against
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
        expect = step.get("expect", {})              # substituted after the action: it may name what the action found ($dragged)
        shown_expect = step.get("expect", {})       # as written: the log never holds what a $name stands for
        # The step as written (with $names, never the secrets behind them) is what the event log records.
        self.events.emit("action", sid, detail={"do": step["do"]})
        if "world.moved" in expect or "world.equipped" in expect or "world.backpack" in expect:
            # measured from where the player stood, and what it wore and carried, before the step acted
            self._moved_from = self._tool_json(GAME_TOOLS["state"], {}).get("player")
        detail = getattr(self, "_do_" + kind.replace(".", "_"))(sid, do, deadline)
        self._last = detail if isinstance(detail, dict) else {}
        self._expect(sid, substitute(expect, self.variables), deadline, shown_expect)
        if step.get("shot"):
            self._screenshot(sid)
        return self._finish(step, watch, True, None, detail)

    def _abort_step(self, step: dict, watch: Stopwatch, reason: str) -> None:
        """The step that was running when the run ended gets its step_end and its row, as failed."""
        self._kill()
        self.steps.append(self._finish(step, watch, False, reason, {}, dump_ui=False))

    def _finish(self, step: dict, watch: Stopwatch, ok: bool, why: str | None, detail: dict, dump_ui: bool = True) -> dict:
        sid = step["id"]
        shown = dict(detail) if isinstance(detail, dict) else {}
        self._mark_frame()
        if why:
            shown["failure"] = why
            dumped = self._dump_ui(sid) if dump_ui else None
            if dumped:
                shown["ui_snapshot"] = dumped
        self.events.emit("step_end", sid, ok=ok, dur_ms=watch.ms(), detail=shown)
        return {"id": sid, "kind": step["do"]["kind"], "ok": ok, "skipped": False, "dur_ms": watch.ms(),
                "detail": why or ""}

    def _dump_ui(self, sid: str) -> str | None:
        """A failed client step keeps the control list (ui/<step>.json) so a selector can be fixed from facts."""
        if self.scenario.surface != "client" or self.session is None:
            return None
        try:
            snapshot = self._snapshot()
            target = self.run_dir / "ui" / f"{sid}.json"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(snapshot, indent=1), encoding="utf-8")
            return f"ui/{sid}.json"
        except Exception:  # a client that is gone must not turn a failed step into a crash
            return None

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
        if name == "world.moved":
            return self._expect_moved(wanted)
        if name in ("world.equipped", "world.backpack"):
            return self._expect_items(name.split(".")[1], wanted)
        if name == "world.mobile":
            return self._expect_mobile(wanted)
        if name == "player":
            player = self._tool_json(GAME_TOOLS["state"], {}).get("player") or {}
            return subset(wanted, player), {k: player.get(k) for k in wanted}
        if name == "combat.attacking":
            return self._expect_attacking(wanted)
        if name == "journal.contains":
            lines = self._tool_json(GAME_TOOLS["state"], {}).get("journal") or []
            found = any(str(wanted) in line for line in lines)
            return found, found                       # the journal is not logged: it may hold other players' words
        if name == "scene":
            scene = self._tool_json(GAME_TOOLS["state"], {}).get("scene")
            return scene == wanted, scene
        if name == "log.contains":
            return self._expect_log(wanted)
        if name == "render_diff":
            seen = self._diff.get("drawn")
            return seen is not None and seen <= int(wanted["max_drawn_diff"]), seen
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

    def _expect_moved(self, wanted: dict) -> tuple[bool, object]:
        """The player is at least `at_least` tiles (the larger of the x and y distance) from where the step began."""
        start = getattr(self, "_moved_from", None)
        player = self._tool_json(GAME_TOOLS["state"], {}).get("player")
        if not start or not player:
            return False, None
        tiles = max(abs(int(player["x"]) - int(start["x"])), abs(int(player["y"]) - int(start["y"])))
        return tiles >= int(wanted.get("at_least", 1)), {"tiles": tiles, "from": start, "to": player}

    def _expect_items(self, which: str, wanted: dict) -> tuple[bool, object]:
        """world.equipped / world.backpack: {"delta": n} against the step's start, and/or {"has" | "lacks": serial}."""
        start = (getattr(self, "_moved_from", None) or {}).get(which)
        now = (self._tool_json(GAME_TOOLS["state"], {}).get("player") or {}).get(which)
        if now is None:
            return False, None
        ok = True
        seen: dict = {"count": len(now)}
        if "delta" in wanted:
            if start is None:
                return False, {"count": len(now), "start": None}
            seen["delta"] = len(now) - len(start)
            ok = ok and seen["delta"] == int(wanted["delta"])
        if "has" in wanted:
            seen["has"] = _serial(wanted["has"]) in now
            ok = ok and seen["has"]
        if "lacks" in wanted:
            seen["lacks"] = _serial(wanted["lacks"]) not in now
            ok = ok and seen["lacks"]
        return ok, seen

    def _find_mobile(self, wanted: dict) -> dict | None:
        """The nearest mobile in guo_state whose name equals `name` or holds `contains`."""
        for m in self._tool_json(GAME_TOOLS["state"], {}).get("mobiles") or []:
            name = m.get("name") or ""
            if ("name" in wanted and name != wanted["name"]) or ("contains" in wanted and str(wanted["contains"]) not in name):
                continue
            return m
        return None

    def _expect_mobile(self, wanted: dict) -> tuple[bool, object]:
        """world.mobile: {"name" | "contains": ..., "within": tiles, "hurt": true (hits below its maximum)}."""
        m = self._find_mobile(wanted)
        if m is None:
            return False, None
        ok = int(m.get("distance", 99)) <= int(wanted.get("within", 18))
        if wanted.get("hurt"):
            ok = ok and 0 < int(m.get("hitsMax") or 0) and int(m.get("hits") or 0) < int(m["hitsMax"])
        return ok, {k: m.get(k) for k in ("name", "distance", "hits", "hitsMax")}

    def _expect_attacking(self, wanted: dict) -> tuple[bool, object]:
        """combat.attacking: the client's last attack is on a mobile named so ({"name" | "contains": ...})."""
        state = self._tool_json(GAME_TOOLS["state"], {})
        target = int((state.get("combat") or {}).get("lastAttack") or 0)
        if not target:
            return False, None
        for m in state.get("mobiles") or []:
            if int(m.get("serial", 0)) == target:
                name = m.get("name") or ""
                ok = name == wanted["name"] if "name" in wanted else str(wanted.get("contains", "")) in name
                return ok, name
        return False, "not near"

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
        if do.get("wait_assets", True) is False:
            return {"launched": True, "assetsReady": False, "waitingForAssets": False}
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
        if any(st["do"]["kind"] == "renderdump" for st in self.scenario.steps):
            self.session.extra_env = {"GUO_RENDER_DUMP_DIR": str(self.run_dir / "render_dump")}
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

    def _do_editor_tool(self, sid: str, do: dict, deadline: float) -> dict:
        if self.scenario.surface != "editor":
            raise StepFailed("editor_tool requires an editor scenario")
        name = do.get("tool", "")
        catalog = self._need_client().request("tools/list").get("tools", [])
        tool = next((t for t in catalog if t.get("name") == name), None)
        if tool is None:
            raise StepFailed(f"unknown editor tool: {name}")
        if tool.get("annotations", {}).get("readOnlyHint") is not True and name not in self.session.preapproved:
            raise StepFailed(f"{name} is not pre-approved; explicitly use --allow-editor-tool {name} for an authorized run")
        until = do.get("until")
        if until is not None and tool.get("annotations", {}).get("readOnlyHint") is not True:
            raise StepFailed("until polling requires a read-only tool; mutations must dispatch once")
        if "refused_contains" in do:
            is_error, text = self._tool(name, do.get("arguments", {}))
            if not is_error or not text.startswith("refused:") or do["refused_contains"] not in text:
                raise StepFailed(f"{name} did not return the expected refusal", {"response": text[:300]})
            return {"refused": True, "reason": text}
        while True:
            result = self._tool_json(name, do.get("arguments", {}))
            if until is None or subset(until, result):
                return result
            if self._clock() >= deadline:
                raise StepFailed(f"{name} did not reach the requested state", {"state": result})
            self._check_program()
            self._sleep(0.25)

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

    # -- the render dump kinds ---------------------------------------------------------------------------------

    def _dump_name(self, do: dict) -> str:
        name = str(do.get("name", ""))
        if not re.fullmatch(r"[A-Za-z0-9_-]+", name):
            raise StepFailed("name must be letters, digits, _ and - (it is a folder name and the word said after renderdump)")
        return name

    def _do_renderdump(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("renderdump")
        name = self._dump_name(do)
        target = self.run_dir / "render_dump" / name / "guo.json"
        self._do_chat(sid, {"text": f"renderdump {name}"}, deadline)
        while True:
            dump = self._read_dump(target)
            if dump is not None:
                break
            if self._clock() >= deadline:
                raise StepFailed(f"no dump appeared at {self._shown(target)} within the step timeout "
                                 "(the client needs GUO_RENDER_DUMP_DIR, which the runner sets for a scenario with a renderdump step)")
            self._check_program()
            self._sleep(0.5)
        objects = sum(len(t.get("objects", [])) for t in dump.get("tiles", []))
        detail = {"dump": self._shown(target), "bytes": target.stat().st_size, "tiles": len(dump.get("tiles", [])), "objects": objects}
        self.events.emit("log", sid, detail=detail)
        self.artifacts.append(f"render_dump/{name}/guo.json")
        return detail

    @staticmethod
    def _read_dump(path: Path) -> dict | None:
        """The dump once it is whole: a file still being written does not parse yet."""
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None

    def _shown(self, path: Path) -> str:
        try:
            return path.resolve().relative_to(self.repo_root.resolve()).as_posix()
        except ValueError:
            return str(path)

    def _do_render_diff(self, sid: str, do: dict, deadline: float) -> dict:
        name = self._dump_name(do)
        guo_path = self.run_dir / "render_dump" / name / "guo.json"
        ref_root = settings.read_setting("GUO_RENDER_REF_DIR", self.repo_root)
        ref_path = (Path(ref_root) if ref_root else self.repo_root / "build" / "render_dump") / name / "cuo.json"
        for label, path in (("this run's GUO dump", guo_path), ("the ClassicUO reference", ref_path)):
            if not path.is_file():
                raise StepFailed(f"{label} is missing: looked for {self._shown(path)}", {"missing": self._shown(path)})
        tool = _render_diff_tool()
        report = tool.compare(tool.load(ref_path), tool.load(guo_path))
        (self.run_dir / "diff.md").write_text("\n".join(report) + "\n", encoding="utf-8")
        self.artifacts.append("diff.md")
        counts, first = _diff_findings(report)
        self._diff = counts
        detail = {"diff": "diff.md", **counts}
        if sum(counts.values()):
            detail["first_mismatches"] = first
            raise StepFailed(f"{counts['map']} map, {counts['fields']} field, {counts['drawn']} drawn mismatches against {self._shown(ref_path)}", detail)
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
        if int(do.get("clear", 0)) > 0:
            self._key("End")                                 # the click may leave the caret mid-text
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

    def _do_ui_hold(self, sid: str, do: dict, deadline: float) -> dict:
        """Hold a key down for a while, then release it: an arrow key held is how the character walks."""
        self._need_game("ui.hold")
        key = do.get("key")
        if not key:
            raise StepFailed("ui.hold needs a key (a Godot key name, e.g. Up, Right)")
        seconds = float(do.get("seconds", 1))
        mods = {m: True for m in ("shift", "ctrl", "alt") if do.get(m)}
        self._input(kind="key", key=str(key), pressed=True, **mods)
        try:
            end = min(self._clock() + seconds, deadline)
            while self._clock() < end:
                self._check_program()
                self._sleep(min(0.25, max(0.0, end - self._clock())))
        finally:
            self._input(kind="key", key=str(key), pressed=False, **mods)   # never leave the key down
        return {"key": key, "held_s": seconds, **mods}

    def _aim(self, control: dict) -> tuple[int, int]:
        """A point the game hit-tests as this control. A classic item is only hit on its drawn pixels, so for a
        control with a serial the pointer tries its centre, then a grid across it, until guo_state.pointer names
        that serial; any other control (or none found) gets its centre."""
        cx, cy = uiquery.center(control)
        serial = control.get("serial")
        if not serial:
            return cx, cy
        x, y, w, h = control["x"], control["y"], control["width"], control["height"]
        points = [(cx, cy)] + [(int(x + w * i / 6), int(y + h * j / 6)) for j in range(1, 6) for i in range(1, 6)]
        for px, py in points:
            self._input(kind="motion", x=px, y=py)
            pointer = self._tool_json(GAME_TOOLS["state"], {}).get("pointer") or {}
            if pointer.get("serial") == serial:
                return px, py
        return cx, cy

    def _do_ui_drag(self, sid: str, do: dict, deadline: float) -> dict:
        """Press on one control, move to another in a few steps and release there: moving or equipping an item."""
        self._need_game("ui.drag")
        if "from" not in do or "to" not in do:
            raise StepFailed("ui.drag needs from and to (control selectors)")
        within = float(do.get("within_s", 5))
        picked = self._wait_control(do["from"], deadline, within)
        if picked.get("serial"):
            self.variables["dragged"] = str(picked["serial"])   # what a later step names as $dragged
        x0, y0 = self._aim(picked) if "from_offset" not in do else uiquery.center(picked)
        x1, y1 = uiquery.center(self._wait_control(do["to"], deadline, within))
        if "from_offset" in do or "to_offset" in do:     # classic pixels from the control's centre, scaled as the gumps are
            scale = float(self._snapshot().get("classicScale") or 1)
            off = do.get("from_offset") or {}
            x0, y0 = x0 + int(float(off.get("x", 0)) * scale), y0 + int(float(off.get("y", 0)) * scale)
            off = do.get("to_offset") or {}
            x1, y1 = x1 + int(float(off.get("x", 0)) * scale), y1 + int(float(off.get("y", 0)) * scale)
        button = str(do.get("button", "Left"))
        steps = max(2, int(do.get("steps", 8)))
        self._input(kind="motion", x=x0, y=y0)
        self._input(kind="button", x=x0, y=y0, button=button, pressed=True)
        try:
            for i in range(1, steps + 1):
                self._input(kind="motion", x=x0 + (x1 - x0) * i // steps, y=y0 + (y1 - y0) * i // steps)
                self._sleep(0.05)
        finally:
            self._input(kind="button", x=x1, y=y1, button=button, pressed=False)   # never leave the button down
        return {"dragged": uiquery.describe(do["from"]), "to": uiquery.describe(do["to"]), "x0": x0, "y0": y0, "x1": x1, "y1": y1}

    def _do_world_click(self, sid: str, do: dict, deadline: float) -> dict:
        """Click a mobile in the world the way a player does: the pointer goes to its feet and up its body until
        the game says the mobile is under it (guo_state.hover), then clicks there (clicks: 2 is a double-click)."""
        self._need_game("world.click")
        wanted = do.get("mobile")
        if not isinstance(wanted, dict) or not ({"name", "contains"} & set(wanted)):
            raise StepFailed('world.click needs mobile: {"name" | "contains": ...}')
        end = min(self._clock() + float(do.get("within_s", 10)), deadline)
        while True:
            mobile = self._find_mobile(wanted)
            if mobile is not None and mobile.get("screen"):
                break
            if self._clock() >= end:
                raise StepFailed(f"no mobile matching {wanted} is near the player")
            self._check_program()
            self._sleep(0.5)
        feet = mobile["screen"]
        for dy in range(0, -100, -8):
            for dx in (0, -8, 8):
                x, y = int(feet["x"]) + dx, int(feet["y"]) + dy
                self._input(kind="motion", x=x, y=y)
                self._sleep(0.1)                        # what is under the pointer is worked out while the world draws
                if int(self._tool_json(GAME_TOOLS["state"], {}).get("hover") or 0) == int(mobile["serial"]):
                    clicks = max(1, int(do.get("clicks", 1)))
                    for _ in range(clicks):
                        self._input(kind="button", x=x, y=y, button="Left", pressed=True)
                        self._input(kind="button", x=x, y=y, button="Left", pressed=False)
                    return {"clicked": mobile.get("name"), "x": x, "y": y, "clicks": clicks}
            if self._clock() >= end:
                break
        raise StepFailed(f"the pointer never found {mobile.get('name')} on screen (feet at {feet['x']},{feet['y']})")

    def _do_net_drop(self, sid: str, do: dict, deadline: float) -> dict:
        """Drop the game connection as a network loss would (guo_net_drop); the client must notice by itself."""
        self._need_game("net.drop")
        is_error, text = self._tool(GAME_TOOLS["net_drop"], {})
        if is_error:
            raise StepFailed(f"net.drop: {text[:200]}")
        return {"dropped": True}

    def _do_chat(self, sid: str, do: dict, deadline: float) -> dict:
        self._need_game("chat")
        if "text" not in do:
            raise StepFailed("chat needs text")
        self._key("Enter")
        self._input(kind="text", text=str(do["text"]))
        self._key("Enter")
        return {"chat_chars": len(str(do["text"]))}


def _serial(value) -> int:
    """A serial written as a number, a decimal string ($dragged) or 0x hex."""
    return uiquery.parse_serial(value)


def _render_diff_tool():
    """tools/render_diff/run.py, loaded by path: its module name `run` is also this runner's."""
    path = Path(__file__).resolve().parents[1] / "render_diff" / "run.py"
    spec = importlib.util.spec_from_file_location("render_diff_tool", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_SECTION = re.compile(r"^## (Map|Fields|Drawn): (\d+)(?: only in ClassicUO, (\d+) only in GUO)?")


def _diff_findings(report: list[str], limit: int = 5) -> tuple[dict, list[str]]:
    """The mismatch counts of a render_diff report (map, fields, drawn; the below-the-ground section is GUO's own
    check, not a difference between the two clients) and the first few mismatch lines."""
    counts = {"map": 0, "fields": 0, "drawn": 0}
    first: list[str] = []
    section = None
    for line in report:
        m = _SECTION.match(line)
        if m:
            section = m.group(1).lower()
            counts[section] = int(m.group(2)) + int(m.group(3) or 0)
        elif line.startswith("## "):
            section = None
        elif section and line.startswith("- ") and not line.startswith("- Drawn by both") and len(first) < limit:
            first.append(line[2:])
    return counts, first
