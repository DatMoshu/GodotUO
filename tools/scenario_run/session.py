"""Starting and stopping the program a scenario drives (this step: the GUO editor), and nothing else.

The runner only ever kills the process it started. The editor opens without taking focus (tools/guo/process.py),
on a scratch settings folder so the owner's editor settings and layout are neither read nor changed, and with the
editor MCP on a fresh loopback port and a one-run token that stay in this process and the child's environment.
"""

from __future__ import annotations

import os
import secrets
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.process import build_child_env, no_activate  # noqa: E402

# The tools a scenario may run in the editor without a dialog: the runner launched this editor for that.
PREAPPROVED = "tour_segment,editor_invoke,editor_screenshot"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def scratch_settings(root: Path, scale: float) -> dict[str, str]:
    """Environment for an editor with its own settings folder: display scale only, single window
    (so popup menus are drawn inside the window that is captured)."""
    folder = root / "Godot"
    folder.mkdir(parents=True, exist_ok=True)
    custom = 2 if scale == 1.0 else 6   # 2 = 100%, 6 = custom
    (folder / "editor_settings-4.7.tres").write_text(
        "[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\n"
        f"interface/editor/appearance/display_scale = {custom}\n"
        f"interface/editor/appearance/custom_display_scale = {scale}\n"
        "interface/multi_window/enable = false\n"
        "interface/editor/display/single_window_mode = true\n",
        encoding="utf-8")
    env = build_child_env()
    env["APPDATA"] = str(root)
    return env


def kill_tree(proc: subprocess.Popen) -> None:
    """Ends the process we started and its children (godot-console starts the engine as a child). Only ours."""
    if proc.poll() is not None:
        return
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
    else:
        proc.kill()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        pass


class EditorSession:
    """One editor started for one run."""

    def __init__(self, cfg, run_dir: Path, scratch: Path, size: str = "3840x2160", scale: float = 1.5):
        self.cfg = cfg
        self.run_dir = run_dir
        self.scratch = scratch
        self.size = size
        self.scale = scale
        self.port = free_port()
        self.token = secrets.token_hex(32)
        self.proc: subprocess.Popen | None = None
        self.log_path = run_dir / "editor.log"
        self._project_godot = cfg.godot_project / "project.godot"
        self._project_before = b""

    def start(self) -> None:
        env = scratch_settings(self.scratch, self.scale)
        env.update({"GUO_EDITOR_MCP_PORT": str(self.port), "GUO_EDITOR_MCP_TOKEN": self.token,
                    "GUO_EDITOR_MCP_PREAPPROVED": PREAPPROVED, "GUO_EDITOR_SCRIPTED": "1",
                    "GUO_EDITOR_WINDOW_SIZE": self.size})
        cmd = [str(self.cfg.godot_console_exe), "--editor", "--path", str(self.cfg.godot_project)]
        self._project_before = self._project_godot.read_bytes()
        log = self.log_path.open("w", encoding="utf-8", errors="replace")
        try:
            self.proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, **no_activate())
        finally:
            log.close()

    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def wait_listening(self, timeout_s: float) -> bool:
        end = time.monotonic() + timeout_s
        while time.monotonic() < end and self.alive():
            with socket.socket() as s:
                s.settimeout(0.5)
                if s.connect_ex(("127.0.0.1", self.port)) == 0:
                    return True
            time.sleep(0.5)
        return False

    def stop(self) -> None:
        if self.proc is not None:
            kill_tree(self.proc)
        # The windowed editor rewrites project.godot on exit; put it back, as tools/editor_tour does.
        try:
            if self._project_before and self._project_godot.read_bytes() != self._project_before:
                self._project_godot.write_bytes(self._project_before)
        except OSError:
            pass
