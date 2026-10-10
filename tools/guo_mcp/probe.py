"""Exercise the real embedded MCP and stdio bridge, without UO data or a shard."""
import argparse
import base64
import json
import os
from pathlib import Path
import queue
import secrets
import socket
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--headed", action="store_true")
    parser.add_argument("--client", action="store_true", help="inspect normal GUO login startup instead of the synthetic input fixture")
    args = parser.parse_args()
    cfg = load_config()
    root = Path(__file__).resolve().parents[2]
    output = root / "build" / "mcp_probe"
    output.mkdir(parents=True, exist_ok=True)
    with socket.socket() as available:
        available.bind(("127.0.0.1", 0))
        port = available.getsockname()[1]
    env = dict(os.environ, GUO_MCP_PORT=str(port), GUO_MCP_TOKEN=secrets.token_hex(32))
    command = [str(cfg.godot_console_exe), "--path", str(root / "godot/GUO")]
    command += [] if args.headed else ["--headless"]
    command += ["--resolution", "640x480"]
    if args.client:
        env["UO_CLIENT_DATA"] = str(cfg.client_data)
        env["UO_CLIENT_VERSION"] = cfg.client_version
        command += ["--", "--no-focus", "--silent", "--no-splash"]
    else:
        command += ["res://dev/McpProbe.tscn"]
    bridge = None
    with (output / ("headed.log" if args.headed else "headless.log")).open("w", encoding="utf-8") as log:
        game = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 30
            while True:
                if game.poll() is not None:
                    raise RuntimeError("Godot exited; inspect probe log")
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=1) as check:
                        # An invalid token must close without returning data.
                        check.sendall(b"invalid-token\n")
                        assert check.recv(1) == b""
                    break
                except (ConnectionRefusedError, TimeoutError):
                    if time.monotonic() > deadline:
                        raise TimeoutError("MCP did not start")
                    time.sleep(0.1)
            bridge = subprocess.Popen([sys.executable, str(root / "tools/guo_mcp/run.py")], env=env,
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, encoding="utf-8")
            replies = queue.Queue()
            def receive():
                for line in bridge.stdout:
                    replies.put(json.loads(line))
            threading.Thread(target=receive, daemon=True).start()
            serial = 0
            def rpc(method, params=None):
                nonlocal serial
                serial += 1
                bridge.stdin.write(json.dumps(dict(jsonrpc="2.0", id=serial, method=method, params=params or {})) + "\n")
                bridge.stdin.flush()
                reply = replies.get(timeout=35)
                assert reply["id"] == serial, reply
                return reply
            def call(name, arguments=None):
                reply = rpc("tools/call", dict(name=name, arguments=arguments or {}))
                assert "result" in reply, reply
                return reply["result"]
            def snapshot():
                return json.loads(call("guo_ui")["content"][0]["text"])
            def click(x, y):
                for event in [dict(kind="motion", x=x, y=y), dict(kind="button", x=x, y=y, button="Left", pressed=True), dict(kind="button", x=x, y=y, button="Left", pressed=False)]:
                    response = call("guo_input", event)
                    assert not response.get("isError"), (event, response, snapshot())
            assert "error" in rpc("tools/list")
            assert rpc("initialize", dict(protocolVersion="2025-06-18", capabilities={}, clientInfo=dict(name="probe", version="1")))["result"]["serverInfo"]["name"] == "guo"
            bridge.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            bridge.stdin.flush()
            assert sorted(t["name"] for t in rpc("tools/list")["result"]["tools"]) == ["guo_input", "guo_overlay", "guo_quit", "guo_screenshot", "guo_state", "guo_ui", "guo_wait"]
            assert "error" in rpc("not/a/method")
            assert call("guo_wait", dict(frames=0))["isError"]
            assert call("guo_input", dict(kind="button", x=-1, y=0, button="Left", pressed=True))["isError"]
            assert call("guo_input", dict(kind="key", key="NotAKey", pressed=True))["isError"]
            assert not call("guo_wait", dict(frames=5)).get("isError")
            # The human-driver overlay: a caption and an outline are accepted, and Space and Esc read back as nothing pressed.
            overlay = lambda **kw: json.loads(call("guo_overlay", kw)["content"][0]["text"])
            assert overlay(text="Probe", step="1/1", control=dict(x=10, y=10, width=40, height=20, label="probe")) == dict(skip=False, abort=False)
            assert overlay(clear=True) == dict(skip=False, abort=False)
            # A second controller is served while the bridge stays connected.
            with socket.create_connection(("127.0.0.1", port), timeout=5) as second:
                lines = second.makefile("rw", encoding="utf-8", newline="\n")
                lines.write(env["GUO_MCP_TOKEN"] + "\n")
                for number, (method, params) in enumerate([("initialize", dict(protocolVersion="2025-06-18", capabilities={}, clientInfo=dict(name="probe2", version="1"))),
                                                           ("tools/call", dict(name="guo_state", arguments={}))], 1):
                    lines.write(json.dumps(dict(jsonrpc="2.0", id=number, method=method, params=params)) + "\n")
                    lines.flush()
                    reply = json.loads(lines.readline())
                    assert reply["id"] == number and "result" in reply, reply
                assert not reply["result"].get("isError")
            assert snapshot()["headless"] == (not args.headed)
            state_now = json.loads(call("guo_state")["content"][0]["text"])
            assert isinstance(state_now["frame"], int) and state_now["frame"] > 0 and "scene" in state_now and "player" in state_now
            if args.client:
                deadline = time.monotonic() + 30
                state = snapshot()
                while state["scene"] != "LoginScene" or not state["controls"]:
                    if time.monotonic() > deadline:
                        raise TimeoutError("Normal GUO login UI did not become inspectable")
                    call("guo_wait", dict(frames=10))
                    state = snapshot()
                assert any(c["system"] == "classic" for c in state["controls"])
                fields = [c for c in state["controls"] if c["system"] == "classic" and c["type"] == "StbTextBox" and c["width"] > 0 and c["height"] > 0]
                assert fields, "Login text fields were not exposed"
                field = fields[0]
                click(int(field["x"] + min(5, field["width"] / 2)), int(field["y"] + min(5, field["height"] / 2)))
                assert any(c["system"] == "classic" and c["type"] == "StbTextBox" and c["focused"] for c in snapshot()["controls"])
                print("PASS: normal GUO startup, LoginScene, classic UI inspection and text-field focus through MCP")
                return
            click(80, 40)
            assert any(c.get("text") == "Clicked" for c in snapshot()["controls"]), snapshot()
            click(80, 110)
            assert not call("guo_input", dict(kind="text", text="MCP ✓")).get("isError")
            assert any(c.get("text") == "Typed: MCP ✓" for c in snapshot()["controls"]), snapshot()
            shot = call("guo_screenshot")
            if args.headed:
                png = base64.b64decode(shot["content"][0]["data"])
                assert png.startswith(b"\x89PNG")
                (output / "headed.png").write_bytes(png)
            else:
                assert shot["isError"]
            print("PASS: authentication, MCP lifecycle/discovery, errors, frame waits, real UI click, Unicode typing, screenshot capability")
        finally:
            if bridge is not None:
                bridge.terminate()
                bridge.wait(timeout=5)
            game.terminate()
            game.wait(timeout=10)


if __name__ == "__main__":
    main()
