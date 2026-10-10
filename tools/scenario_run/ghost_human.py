"""A stand-in for a person, to test the human driver (`run.py SCENARIO --driver human --ghost-human`).

A second process, with its own connection to the game's MCP, that does what a person would: it watches the overlay's
step label ("3/18", the Label named HumanOverlayStep in `guo_ui`) and, when the step it shows comes up, performs that
step's `do` through `guo_input`, the way the AI driver does (it borrows the AI runner's own ui.click / ui.fill / ui.key /
chat code). It never reads an expectation, never presses Space or Esc, and never touches `guo_overlay`, so the human
driver's polling and key handling are what the run exercises.

    python ghost_human.py SCENARIO_FILE PORT        # the token is in GUO_MCP_TOKEN, the scenario's $names in GUO_SCENARIO_*
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import events as ev  # noqa: E402
import scenario as sc  # noqa: E402
from driver import Runner, StepFailed  # noqa: E402
from mcp_client import McpClient  # noqa: E402

ACTION_KINDS = {"ui.click", "ui.fill", "ui.key", "chat"}
LABEL = "HumanOverlayStep"
POLL_S = 0.25


def current_step(snapshot: dict) -> int:
    """The step number the overlay shows, 0 when it shows none."""
    for c in snapshot.get("controls", []):
        if c.get("name") == LABEL and c.get("text"):
            m = re.match(r"\s*(\d+)\s*/", c["text"])
            if m:
                return int(m.group(1))
    return 0


def ghost(scenario: sc.Scenario, client, log: ev.EventLog, run_dir: Path, *, clock=time.monotonic, sleep=time.sleep,
          give_up_s: float = 900.0) -> int:
    """Performs the scenario's action steps in order, each once the overlay has reached it. Returns how many it did."""
    runner = Runner(scenario, run_dir, log, repo_root=run_dir, variables={}, clock=clock, sleep=sleep)
    runner.client = client
    runner._run_deadline = clock() + give_up_s
    todo = [(i, s) for i, s in enumerate(scenario.steps, 1) if s["do"]["kind"] in ACTION_KINDS and not s.get("ai_only")]
    done = 0
    end = clock() + give_up_s
    while todo and clock() < end:
        shown = current_step(runner._tool_json("guo_ui", {}))
        while todo and todo[0][0] <= shown:
            number, step = todo.pop(0)
            do = sc.substitute(step["do"], runner.variables)
            try:
                getattr(runner, "_do_" + step["do"]["kind"].replace(".", "_"))(step["id"], do, clock() + 30)
            except StepFailed as ex:
                log.emit("error", step["id"], detail={"ghost": ex.why})
                return done
            log.emit("log", step["id"], detail={"note": "ghost did the step"})
            done += 1
        sleep(POLL_S)
    return done


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2
    scenario = sc.load(Path(argv[0]))
    run_dir = Path.cwd()                          # the run folder (GhostProcess starts the child there)
    token = os.environ.get("GUO_MCP_TOKEN", "")
    client = McpClient(int(argv[1]), token)
    log = ev.EventLog(Path(os.environ.get("GUO_GHOST_EVENTS", os.devnull)), "ghost", "human")
    try:
        ghost(scenario, client, log, run_dir)
    finally:
        client.close()
        log.close()
    return 0


class GhostProcess:
    """What HumanRunner starts after the launch step: the ghost as a child process, ended with the run."""

    def __init__(self, scenario_file: Path, run_dir: Path, variables: dict[str, str]):
        self.scenario_file = scenario_file
        self.run_dir = run_dir
        self.variables = variables
        self.proc: subprocess.Popen | None = None

    def start(self, runner: Runner) -> None:
        session = runner.session
        env = dict(os.environ)
        env.update({"GUO_MCP_TOKEN": session.token, "GUO_GHOST_EVENTS": str(self.run_dir / "ghost.events.jsonl")})
        for name, value in self.variables.items():                 # the scenario's $names reach the child by environment, never argv
            env["GUO_SCENARIO_" + name.upper()] = value
        log = (self.run_dir / "ghost.log").open("w", encoding="utf-8", errors="replace")
        try:
            self.proc = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), str(self.scenario_file), str(session.port)],
                                         stdout=log, stderr=subprocess.STDOUT, env=env, cwd=str(self.run_dir))
        finally:
            log.close()

    def stop(self) -> None:
        if self.proc is not None and self.proc.poll() is None:
            self.proc.kill()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
        self.proc = None


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
