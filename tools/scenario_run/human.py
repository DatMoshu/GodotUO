"""The human driver: a person performs the steps, the runner shows them and checks them.

`--driver human` skips each step's `do`. The runner sends the step's `say` (or its id) to the client's overlay
(`guo_overlay`, docs/data_formats.md section 29), outlines the control the step names, and polls the step's `expect`
with the AI driver's own code. When the expectation holds the run moves on by itself. Space skips the step (logged
`skipped`), Esc aborts the run (FAIL, `aborted`). A step marked `ai_only` is skipped and logged.

Three things stay the runner's, because nobody could follow them: `launch` (there is no program to follow without it),
`note` and `shot`. A step's `shot: true` is a recording, not an action, so it is taken as in an AI run. A step with
no expectation (a caption, a `wait`) stays on screen for `dwell_s` seconds and then moves on: what the person did
stays done, so the next expectation sees it.

The windows are the AI driver's, times HUMAN_FACTOR: a person is slower than a script (the run's `run_s` is scaled in run.py).
"""

from __future__ import annotations

import json

import uiquery
from driver import RunAborted, Runner, StepFailed
from events import Stopwatch

OVERLAY_TOOLS = {"client": "guo_overlay", "editor": "human_overlay"}     # the MCP tool that draws the caption, per surface
HUMAN_FACTOR = 3.0           # every window (run and step) is this many times the AI driver's
DWELL_S = 3.0                # a step with nothing to check stays on screen this long
MIN_SHOW_S = 1.0             # a caption whose expectation already holds is still shown this long
POLL_S = 0.5
HIGHLIGHT_REFRESH_S = 2.0    # how often an outlined control's position is looked up again
RUNNER_OWNED = {"launch", "note", "shot"}     # the runner performs these in a human run
CONTROL_KINDS = {"ui.click", "ui.fill"}       # the kinds whose `control` is outlined (client scenarios only)
# Editor tour segments that only show a view and check it: the person watches, so the runner plays them. Every other
# segment stamps, jumps, types, installs or talks to a shard on its own, which nobody could follow: it is `ai_only`.
PASSIVE_SEGMENTS = {"layout", "gumps", "anims", "pick"}


class HumanRunner(Runner):
    """A scenario run where the person does the steps. Everything not about the steps is the AI runner's."""

    def __init__(self, *args, clean: bool = False, dwell_s: float = DWELL_S, min_show_s: float = MIN_SHOW_S,
                 factor: float = HUMAN_FACTOR, ghost=None, **kwargs):
        super().__init__(*args, **kwargs)
        self.clean = clean               # --clean: the overlay draws nothing (Space and Esc still work)
        self.dwell_s = dwell_s
        self.min_show_s = min_show_s
        self.factor = factor
        self.ghost = ghost               # a stand-in person for tests (ghost_human.py); started after the launch step
        self._outlined_at = -1e9
        self._outline: dict | None = None

    # -- the steps ---------------------------------------------------------------------------------------------

    def _step_body(self, step: dict, sid: str, kind: str, watch: Stopwatch, deadline: float) -> dict:
        if step.get("ai_only"):
            return self._skipped(step, watch, "ai_only")
        if kind == "tour_segment":
            if step["do"].get("id") not in PASSIVE_SEGMENTS:
                return self._skipped(step, watch, "ai_only: the segment drives the editor itself")
            return super()._step_body(step, sid, kind, watch, deadline)      # a passive segment: the person watches it play
        if kind in RUNNER_OWNED:
            row = super()._step_body(step, sid, kind, watch, deadline)
            if kind == "launch" and row["ok"] and self.ghost is not None:
                self.ghost.start(self)
            return row
        return self._follow(step, sid, kind, watch)

    def _skipped(self, step: dict, watch: Stopwatch, why: str) -> dict:
        sid = step["id"]
        self.events.emit("log", sid, detail={"note": f"skipped ({why})"})
        self._mark_frame()
        self.events.emit("step_end", sid, ok=None, dur_ms=watch.ms(), detail={"skipped": why})
        return {"id": sid, "kind": step["do"]["kind"], "ok": None, "skipped": True, "dur_ms": watch.ms(),
                "detail": f"skipped ({why})"}

    def _follow(self, step: dict, sid: str, kind: str, watch: Stopwatch) -> dict:
        """Shows the step and waits until its expectation holds, Space skips it, or Esc aborts the run."""
        expect = step.get("expect", {})
        conditions = {k: v for k, v in expect.items() if k != "within_s"}
        wanted = {k: self._substituted(v) for k, v in conditions.items()}
        self.events.emit("action", sid, detail={"do": step["do"], "by": "human"})
        window = float(step.get("timeout_s") or self.scenario.timeouts["step_s"]) * self.factor
        started = self._clock()
        end = min(started + window, self._run_deadline)
        dwell = max(self.dwell_s, float(step["do"].get("seconds", 0))) if kind == "wait" else self.dwell_s
        self._outline = None
        self._outlined_at = -1e9
        self._show(step)
        while True:
            self._check_run_deadline()
            keys = self._poll_keys()
            if keys.get("abort"):
                raise RunAborted(f"aborted by the person (Esc) in step {sid}", "failed")
            if keys.get("skip"):
                return self._skipped(step, watch, "space")
            self._outline_control(step, sid)
            elapsed = self._clock() - started
            if wanted:
                holds, observed = self._conditions_hold(wanted)
                if holds and elapsed >= self.min_show_s:
                    self.events.emit("expect", sid, ok=True, detail={"expect": conditions, "observed": observed})
                    break
                if self._clock() >= end:
                    self.events.emit("expect", sid, ok=False, detail={"expect": conditions, "observed": observed})
                    raise StepFailed("expectation not met", {"expect": conditions, "observed": observed})
            elif elapsed >= dwell:
                break
            self._check_program()
            self._sleep(POLL_S)
        if step.get("shot"):
            self._screenshot(sid)
        return self._finish(step, watch, True, None, {})

    def _substituted(self, value):
        from scenario import substitute
        return substitute(value, self.variables)

    # -- the overlay -------------------------------------------------------------------------------------------

    @property
    def overlay_tool(self) -> str:
        return OVERLAY_TOOLS[self.scenario.surface]

    def _overlay(self, **args) -> dict:
        is_error, text = self._tool(self.overlay_tool, args)
        if is_error:
            raise StepFailed(f"{self.overlay_tool} failed: {text[:200]}")
        try:
            reply = json.loads(text)
        except ValueError as ex:
            raise StepFailed(f"{self.overlay_tool} returned something that is not JSON: {text[:200]}") from ex
        return reply if isinstance(reply, dict) else {}

    def _show(self, step: dict) -> None:
        steps = self.scenario.steps
        number = next((i for i, s in enumerate(steps, 1) if s["id"] == step["id"]), 0)
        self._overlay(text=step.get("say") or step["id"], step=f"{number}/{len(steps)}", hide=self.clean)

    def _poll_keys(self) -> dict:
        return self._overlay()           # no arguments: only reads (and clears) Space and Esc

    def _outline_control(self, step: dict, sid: str) -> None:
        """Outlines the control the step names, looked up again every HIGHLIGHT_REFRESH_S while the step is on screen."""
        do = step["do"]
        if self.scenario.surface != "client" or do["kind"] not in CONTROL_KINDS or not do.get("control") or self._clock() - self._outlined_at < HIGHLIGHT_REFRESH_S:
            return
        self._outlined_at = self._clock()
        control = uiquery.find(self._snapshot(), do["control"])
        if control is None:
            return
        box = {k: int(control[k]) for k in ("x", "y", "width", "height")}
        if box != self._outline:
            self._outline = box
            self._overlay(control={**box, "label": uiquery.describe(do["control"])[:40]})

    def _stop_program(self) -> None:
        if self.ghost is not None:
            self.ghost.stop()
        if self.client is not None:
            try:
                self.client.call(self.overlay_tool, {"clear": True}, timeout=5)
            except Exception:
                pass
        super()._stop_program()
