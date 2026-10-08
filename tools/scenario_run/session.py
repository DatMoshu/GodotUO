"""Starting and stopping the program a scenario drives (the GUO editor or the GUO client), and nothing else.

The runner only ever kills the process it started. The editor opens without taking focus (tools/guo/process.py),
on a scratch settings folder so the owner's editor settings and layout are neither read nor changed, and with the
editor MCP on a fresh loopback port and a one-run token that stay in this process and the child's environment.
"""

from __future__ import annotations

import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.process import build_child_env, no_activate  # noqa: E402

# The tools a scenario may run in the editor without a dialog: the runner launched this editor for that.
# editor_invoke is not in it: an F3 action can do more than a scenario needs, so it keeps its approval dialog.
PREAPPROVED = "tour_segment,editor_screenshot"


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


def remove_tree(path: Path, tries: int = 10, wait_s: float = 0.5) -> bool:
    """Remove a folder the client may still hold for a moment after its process tree is gone (its scratch/<pid>/Data
    folders were still open at the first try); retry briefly, then give up quietly."""
    for i in range(tries):
        try:
            shutil.rmtree(path)
        except FileNotFoundError:
            return True
        except OSError:
            if i + 1 < tries:
                time.sleep(wait_s)
            continue
        return True
    return not path.exists()


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
        self.preapproved = set(PREAPPROVED.split(","))
        self.preapproved = set(PREAPPROVED.split(","))
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


class ClientSession:
    """One GUO client started for one run, through launchers/game/play.bat (so it resolves the UO data exactly as a player's
    launch does), with the game MCP on a fresh loopback port and a one-run token, and, when recording, the engine's
    MovieWriter on (capture.py). The runner only ever ends the process tree it started."""

    def __init__(self, cfg, run_dir: Path, record: bool, size: str | None = None):
        self.cfg = cfg
        self.run_dir = run_dir
        self.record = record
        self.size = size
        self.port = free_port()
        self.token = secrets.token_hex(32)
        self.proc: subprocess.Popen | None = None
        self.extra_args: list[str] = []
        self.extra_settings: dict = {}
        self.shard: tuple[str, int] | None = None     # --shard: the address the client connects to (UO_SHARD_HOST / UO_SHARD_PORT)
        self._home: Path | None = None
        self.log_path = run_dir / "client.log"
        self.avi = run_dir / "raw" / "run.avi"
        import capture
        self._watch = capture.StallWatch(self.avi, time.monotonic)

    def stalled(self) -> bool:
        """A recording whose movie file stopped growing: the engine is wedged (capture.STALL_S)."""
        return self.record and self.alive() and self._watch.stalled()
        import capture
        self._watch = capture.StallWatch(self.avi, time.monotonic)

    def stalled(self) -> bool:
        """A recording whose movie file stopped growing: the engine is wedged (capture.STALL_S)."""
        return self.record and self.alive() and self._watch.stalled()

    def start(self) -> None:
        import capture
        env = build_child_env()
        env.update({"GUO_MCP_PORT": str(self.port), "GUO_MCP_TOKEN": self.token})
        if self.shard is not None:
            env.update({"UO_SHARD_HOST": self.shard[0], "UO_SHARD_PORT": str(self.shard[1])})
        if self.extra_settings:
            env["UO_CACHE_DIR"] = str(self._make_home())
        engine = ["--resolution", self.size] if self.size else []      # a movie is the project's 1280x720 whatever this says
        if self.record:
            self.avi.parent.mkdir(parents=True, exist_ok=True)
            engine.insert(0, capture.engine_args(self.avi))
        env["GUO_ENGINE_ARGS"] = " ".join(engine)
        args = ["--no-focus"] + (["--window-size", self.size.replace("x", ",")] if self.size else []) + ([] if self.record else ["--silent"]) + list(self.extra_args)   # a recording keeps its audio
        cmd = [str(self.cfg.root / "launchers" / "game" / "play.bat"), *args]
        log = self.log_path.open("w", encoding="utf-8", errors="replace")
        try:
            self.proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, cwd=str(self.cfg.root), **no_activate())
        finally:
            log.close()

    def _make_home(self) -> Path:
        """A client home for this run, made when a launch step names settings: the client keeps settings.json in the parent
        of its cache folder, so a home of the run's own holds a copy of the usual settings.json with those values set. The
        usual file is only read. The copy can carry the owner's saved account, so it lives in a temp folder, not the run
        folder, and stop() removes it."""
        self._home = Path(tempfile.mkdtemp(prefix="guo_run_home_"))
        base: dict = {}
        usual = Path(self.cfg.cache_dir).parent / "settings.json"
        if usual.is_file():
            try:
                base = json.loads(usual.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                base = {}
        base.update(self.extra_settings)
        (self._home / "settings.json").write_text(json.dumps(base), encoding="utf-8")
        (self._home / "cache").mkdir()
        return self._home / "cache"

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

    def wait_exit(self, timeout_s: float) -> bool:
        if self.proc is None:
            return True
        try:
            self.proc.wait(timeout=timeout_s)
            return True
        except subprocess.TimeoutExpired:
            return False

    def stop(self) -> None:
        if self.proc is not None:
            kill_tree(self.proc)
        if self._home is not None:
            remove_tree(self._home)
            self._home = None
