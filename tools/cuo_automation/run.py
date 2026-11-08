r"""Drive stock ClassicUO through the same MCP tools GUO has (guo_state, guo_input, ...).

    python tools/cuo_automation/run.py proof [--build] [--run-dir DIR] [--size 1280,720]
    python tools/cuo_automation/run.py selftest

`proof` launches the injected, out-of-tree ClassicUO (tools/render_dump/inject.targets
compiles tools/cuo_automation/CuoMcpHost.cs into it) with its window unfocused,
connects tools/scenario_run's McpClient, waits for the player, sends
Enter / text / Enter for `[go 1602 1591` and `[globallight 0`, takes a screenshot,
checks the position moved, calls guo_quit and checks the process exits.

Other code uses `start_client()` to get a connected McpClient and the process.
The MCP token is made per run, lives in the child's environment and in this
process, and is never printed or written down.
"""
from __future__ import annotations

import argparse
import datetime
import importlib.util
import json
import os
import secrets
import socket
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(TOOLS / "scenario_run"))

from guo.config import Config, load_config  # noqa: E402
from mcp_client import McpClient, McpError  # noqa: E402


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def ab_compare():
    """tools/ab_compare/run.py: the out-of-tree build and the ClassicUO settings and profile."""
    return sys.modules.get("ab_compare") or _load("ab_compare", TOOLS / "ab_compare" / "run.py")


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Cuo:
    """A running injected ClassicUO and the MCP connection to it."""

    def __init__(self, proc: subprocess.Popen, client: McpClient, log: Path, secrets_: list[str]):
        self.proc = proc
        self.client = client
        self.log = log
        self.secrets = secrets_

    def state(self) -> dict:
        return self.client.call_json("guo_state")

    def key(self, key: str, **mods) -> None:
        for pressed in (True, False):
            self._input(kind="key", key=key, pressed=pressed, **mods)

    def chat(self, text: str) -> None:
        """Enter, the text, Enter: what tools/scenario_run/driver.py _do_chat sends."""
        self.key("Enter")
        self._input(kind="text", text=text)
        self.key("Enter")

    def _input(self, **args) -> None:
        is_error, text = self.client.call("guo_input", args)
        if is_error:
            raise McpError(f"guo_input {args.get('kind')} failed: {text[:200]}")

    def wait_player(self, timeout: float) -> dict:
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if self.proc.poll() is not None:
                raise McpError("ClassicUO exited before the player was in the world")
            state = self.state()
            if state.get("player"):
                return state
            time.sleep(1.0)
        raise McpError(f"no player in the world after {timeout:.0f} s (scene {self.state().get('scene')})")

    def screenshot(self) -> bytes:
        import base64

        is_error, content = self.client.call_content("guo_screenshot")
        images = [c for c in content if c.get("type") == "image"]
        if is_error or not images:
            raise McpError("guo_screenshot returned no image")
        return base64.b64decode(images[0]["data"])

    def quit(self, timeout: float = 30.0) -> int | None:
        """guo_quit, then the exit code once the process is gone (None if it had to be killed)."""
        try:
            self.client.call("guo_quit", timeout=20)
        except McpError:
            pass  # the connection closes with the client
        self.client.close()
        try:
            return self.proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None
        finally:
            scrub(self.log, self.secrets)


def start_client(cfg: Config, log_dir: Path, size: tuple[int, int] = (1280, 720), tuck: bool = True,
                 extra_env: dict | None = None) -> Cuo:
    """Launch the injected ClassicUO unfocused, logged in as the shard owner, and connect to its MCP server."""
    ab = ab_compare()
    exe = cfg.upstream_exe
    if not exe.exists():
        raise McpError(f"{exe} is missing; run: python tools/cuo_automation/run.py proof --build")
    port, token = free_port(), secrets.token_hex(32)
    character = os.environ.get("CUO_AUTOMATION_CHARACTER") or ab.last_played(cfg) or ""
    conf = ab.write_cuo_config(cfg, character, size)
    log_dir.mkdir(parents=True, exist_ok=True)
    log = log_dir / "cuo.log"
    env = {**os.environ, "FNA_GRAPHICS_ENABLE_HIGHDPI": "1", "__COMPAT_LAYER": "HighDpiAware",
           "SDL_WINDOW_ACTIVATE_WHEN_SHOWN": "0", "GUO_MCP_PORT": str(port), "GUO_MCP_TOKEN": token,
           **(extra_env or {})}
    proc = subprocess.Popen(
        [str(exe), "-settings", str(conf / "settings.json"),
         "-username", cfg.shard_owner, "-password", cfg.shard_owner_password,
         "-ip", cfg.shard_host, "-port", str(cfg.shard_port), "-autologin", "true", "-skiploginscreen",
         "-lastcharactername", character, "-music", "false"],
        cwd=str(cfg.upstream_build), stdout=log.open("w", encoding="utf-8", errors="replace"),
        stderr=subprocess.STDOUT, env=env)
    if tuck:
        visual_ab = sys.modules.get("visual_ab_run") or _load("visual_ab_run", TOOLS / "visual_ab" / "run.py")
        visual_ab.Tucked(proc).start()  # keeps the window off the owner's screen; never takes focus
    deadline = time.monotonic() + 60
    while True:
        if proc.poll() is not None:
            raise McpError(f"ClassicUO exited with {proc.returncode} before its MCP server answered; see {log}")
        try:
            return Cuo(proc, McpClient(port, token), log, [cfg.shard_owner_password, token])
        except (OSError, McpError) as ex:
            last = type(ex).__name__ + ": " + str(ex)[:100]
            if time.monotonic() > deadline:
                proc.kill()
                proc.wait()
                scrub(log, [cfg.shard_owner_password, token])
                raise McpError(f"the MCP server did not answer within 60 s ({last})") from None
            time.sleep(0.5)


def scrub(path: Path, secrets_: list[str]) -> None:
    """ClassicUO echoes its command line, password included, into its own log; take every secret out."""
    if not path.exists():
        return
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = ["ARG: password, VALUE: <redacted>" if "ARG: password" in line else line for line in text.splitlines()]
    text = chr(10).join(lines)
    for s in secrets_:
        if s:
            text = text.replace(s, "<redacted>")
    path.write_text(text, encoding="utf-8")


def _leaks(path: Path, secrets_: list[str]) -> bool:
    text = path.read_text(encoding="utf-8", errors="replace")
    return any(s and s in text for s in secrets_)


def proof(args) -> int:
    cfg = load_config()
    if args.build:
        ab_compare().build_cuo(cfg)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M")
    run_dir = Path(args.run_dir) if args.run_dir else cfg.build / "cuo_automation" / "proof"
    stills, logs = run_dir / "stills", run_dir / "logs"
    stills.mkdir(parents=True, exist_ok=True)
    size = tuple(int(v) for v in args.size.split(","))
    notes: dict = {"checks": {}}
    cuo = start_client(cfg, logs, size)
    code = None
    try:
        tools = cuo.client.request("tools/list")["tools"]
        notes["tools"] = sorted(t["name"] for t in tools)
        before = cuo.wait_player(args.login_timeout)
        notes["in_world"] = before
        cuo.client.call("guo_wait", {"frames": 120})
        ui = cuo.client.call_json("guo_ui")
        notes["gumps_in_world"] = sorted({c["type"] for c in ui["controls"]})
        cuo.chat("[go 1602 1591")
        deadline = time.monotonic() + 30
        moved = before
        while time.monotonic() < deadline:
            moved = cuo.state()
            if moved["player"] and (moved["player"]["x"], moved["player"]["y"]) != (before["player"]["x"], before["player"]["y"]):
                break
            cuo.client.call("guo_wait", {"frames": 30})
        notes["after_go"] = moved
        notes["checks"]["position_changed"] = moved["player"] != before["player"]
        notes["checks"]["at_go_target"] = bool(moved["player"]) and abs(moved["player"]["x"] - 1602) <= 1 and abs(moved["player"]["y"] - 1591) <= 1
        cuo.chat("[globallight 0")
        # The jump fades the view in; give it time to settle.
        time.sleep(args.settle)
        cuo.client.call("guo_wait", {"frames": 60})
        png = cuo.screenshot()
        shot = stills / f"guo_cuo-automation_street-1602-1591_still_{stamp}.png"
        shot.write_bytes(png)
        notes["screenshot"] = {"file": shot.name, "bytes": len(png), "png": png[:8] == b"\x89PNG\r\n\x1a\n"}
        notes["checks"]["screenshot_is_png"] = notes["screenshot"]["png"]
    finally:
        code = cuo.quit()
    notes["exit_code"] = code
    notes["checks"]["exited_cleanly"] = code == 0
    notes["checks"]["no_secret_in_log"] = not _leaks(cuo.log, [cfg.shard_owner_password])
    (logs / "proof.json").write_text(json.dumps(notes, indent=2), encoding="utf-8")
    print(json.dumps({"checks": notes["checks"], "exit_code": code, "after_go": notes.get("after_go", {}).get("player")}, indent=2))
    return 0 if all(notes["checks"].values()) else 1


def selftest(_args) -> int:
    import pytest

    return pytest.main([str(HERE / "test_cuo_automation.py"), "-q"])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("proof", help="launch, drive and quit the injected ClassicUO (needs the shard)")
    p.add_argument("--build", action="store_true", help="build the injected ClassicUO first")
    p.add_argument("--run-dir", help="where stills/ and logs/ go (default: build/cuo_automation/proof)")
    p.add_argument("--size", default="1280,720", help="window size")
    p.add_argument("--login-timeout", type=float, default=120.0)
    p.add_argument("--settle", type=float, default=8.0, help="seconds to wait after the jump before the screenshot")
    p.set_defaults(fn=proof)
    t = sub.add_parser("selftest", help="the tests that need no shard and no ClassicUO build")
    t.set_defaults(fn=selftest)
    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
