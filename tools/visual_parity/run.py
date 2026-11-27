"""Capture visible gumps from already-running GUO and ClassicUO MCP clients.

    python tools/visual_parity/run.py capture --out <external-run-folder>

Requires GUO_MCP_PORT/TOKEN and CUO_MCP_PORT/TOKEN. Gump art ID inventories
and raw ID sheets remain the responsibility of the pinned guoasset MCP tools.
"""
from __future__ import annotations

import argparse
import base64
import importlib.util
from io import BytesIO
import json
import os
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(TOOLS / "scenario_run"))
from mcp_client import McpClient, McpError  # noqa: E402
from guo.config import load_config  # noqa: E402


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise McpError(f"cannot load helper {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def parse_size(value: str) -> tuple[int, int]:
    parts = value.lower().replace("x", ",").split(",")
    if len(parts) != 2 or any(not p.strip().isdigit() for p in parts):
        raise McpError("--size must be WIDTHxHEIGHT, e.g. 1280x720")
    width, height = (int(p) for p in parts)
    if width < 320 or height < 240:
        raise McpError("--size must be at least 320x240")
    return width, height


def client(prefix: str) -> McpClient:
    port = os.environ.get(f"{prefix}_MCP_PORT")
    token = os.environ.get(f"{prefix}_MCP_TOKEN")
    if not port or not token:
        raise McpError(f"Set {prefix}_MCP_PORT and {prefix}_MCP_TOKEN for the running client.")
    return McpClient(int(port), token)


def image(client_: McpClient) -> bytes:
    failed, content = client_.call_content("guo_screenshot")
    blocks = [b for b in content if b.get("type") == "image"]
    if failed or not blocks:
        raise McpError("guo_screenshot returned no PNG image")
    return base64.b64decode(blocks[0]["data"])


def side_by_side(cuo, guo):
    from PIL import Image, ImageDraw

    gap = 24
    label_height = 28
    sheet = Image.new(
        "RGBA",
        (cuo.width + gap + guo.width, max(cuo.height, guo.height) + label_height),
        (24, 28, 32, 255),
    )
    draw = ImageDraw.Draw(sheet)
    draw.text((4, 4), "ClassicUO", fill="white")
    draw.text((cuo.width + gap + 4, 4), "GUO", fill="white")
    sheet.alpha_composite(cuo, (0, label_height))
    sheet.alpha_composite(guo, (cuo.width + gap, label_height))
    return sheet


def capture(args: argparse.Namespace, clients=None) -> int:
    try:
        from PIL import Image
    except ImportError as exc:
        raise McpError("Pillow is required for gump crops; install it in the Python environment.") from exc

    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    owns_clients = clients is None
    if owns_clients:
        clients = {"cuo": client("CUO")}
    manifest = {"created_utc": datetime.now(timezone.utc).isoformat(), "clients": {}, "gumps": {}}
    frames = {}
    roots = {}
    try:
        if owns_clients:
            clients["guo"] = client("GUO")
        for name, mcp in clients.items():
            failed, reply = mcp.call("guo_wait", {"frames": args.settle_frames})
            if failed:
                raise McpError(f"{name} could not settle before capture: {reply[:200]}")
            ui = mcp.call_json("guo_ui")
            png = image(mcp)
            (out / f"{name}.png").write_bytes(png)
            (out / f"{name}.ui.json").write_text(json.dumps(ui, indent=2), encoding="utf-8")
            frame = frames[name] = Image.open(BytesIO(png)).convert("RGBA")
            manifest["clients"][name] = {k: ui.get(k) for k in ("scene", "width", "height", "classicScale")}

            grouped = roots[name] = {}
            for control in ui.get("controls", []):
                gump = control.get("gump")
                if control.get("system") != "classic" or not gump or control.get("type") != gump:
                    continue
                x, y, w, h = (int(control[key]) for key in ("x", "y", "width", "height"))
                box = max(0, x), max(0, y), min(frame.width, x + w), min(frame.height, y + h)
                if box[2] > box[0] and box[3] > box[1]:
                    grouped.setdefault(gump, []).append(((x, y, w, h), box))
            for instances in grouped.values():
                instances.sort()

        sheet = out / "frames.side-by-side.png"
        side_by_side(frames["cuo"], frames["guo"]).save(sheet)
        manifest["frames_side_by_side"] = sheet.name
        manifest["only_cuo"] = []
        manifest["only_guo"] = []
        for gump in sorted(set(roots["cuo"]) | set(roots["guo"])):
            cuo_instances = roots["cuo"].get(gump, [])
            guo_instances = roots["guo"].get(gump, [])
            for index in range(max(len(cuo_instances), len(guo_instances))):
                key = f"{gump}-{index + 1:02}" if index else gump
                if index >= len(cuo_instances):
                    manifest["only_guo"].append(key)
                    continue
                if index >= len(guo_instances):
                    manifest["only_cuo"].append(key)
                    continue
                pair = manifest["gumps"][key] = {}
                crops = {}
                for name, (bounds, box) in (("cuo", cuo_instances[index]), ("guo", guo_instances[index])):
                    crop = crops[name] = frames[name].crop(box)
                    path = out / f"{key}.{name}.png"
                    crop.save(path)
                    pair[name] = {"file": path.name, "bounds": bounds, "capture_bounds": box}
                combined = out / f"{key}.side-by-side.png"
                side_by_side(crops["cuo"], crops["guo"]).save(combined)
                pair["side_by_side"] = combined.name

        (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        print(json.dumps({"out": str(out), "paired_gumps": list(manifest["gumps"]),
                          "only_cuo": manifest["only_cuo"], "only_guo": manifest["only_guo"]}, indent=2))
        return 0
    finally:
        if owns_clients:
            for mcp in clients.values():
                mcp.close()


def asset_export(args: argparse.Namespace) -> int:
    """Call the built guoasset stdio MCP for a pinned-loader batch gump export."""
    cfg = load_config()
    dll = Path(args.server) if args.server else cfg.build / "guoasset" / "bin" / "GUO.AssetMcp" / "release" / "guoasset.dll"
    if not dll.is_file():
        raise McpError(f"{dll} is missing; build it with launchers\\dev\\build_guoasset.bat")
    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    proc = subprocess.Popen(["dotnet", str(dll)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    try:
        assert proc.stdin and proc.stdout
        next_id = 0

        def rpc(method: str, params: dict | None = None, wait: bool = True) -> dict | None:
            nonlocal next_id
            msg = {"jsonrpc": "2.0", "method": method, "params": params or {}}
            wanted = None
            if wait:
                next_id += 1
                wanted = next_id
                msg["id"] = wanted
            proc.stdin.write(json.dumps(msg) + "\n")
            proc.stdin.flush()
            if wanted is None:
                return None
            while True:
                line = proc.stdout.readline()
                if not line:
                    raise McpError("guoasset closed its MCP stream")
                reply = json.loads(line)
                if reply.get("id") == wanted:
                    if "error" in reply:
                        raise McpError("guoasset MCP error: " + str(reply["error"].get("message", "unknown error")))
                    return reply["result"]

        rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "guo-visual-parity", "version": "1.0"}})
        rpc("notifications/initialized", wait=False)
        result = rpc("tools/call", {"name": "export_art", "arguments": {"kind": "gump", "from": str(args.start), "to": str(args.end), "outDir": str(out)}})
        blocks = result.get("content", []) if result else []
        text = "\n".join(b.get("text", "") for b in blocks)
        if result.get("isError"):
            raise McpError("guoasset export_art failed: " + text[:400])
        print(json.dumps({"folder": str(out), "range": [args.start, args.end], "result": text}, indent=2))
        return 0
    finally:
        if proc.stdin:
            proc.stdin.close()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.terminate()
            proc.wait(timeout=5)


def drive(args: argparse.Namespace) -> int:
    """Replay deterministic MCP input steps against both clients from a JSON plan."""
    Path(args.out).mkdir(parents=True, exist_ok=True)
    plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
    clients = {"cuo": client("CUO"), "guo": client("GUO")}
    result = {"manifest": str(Path(args.plan).resolve()), "scenarios": [], "summary": {"asserted": 0, "executed_unasserted": 0, "skipped": 0, "failed": 0}}
    try:
        for scenario in plan["scenarios"]:
            sid = scenario["id"]
            sresult = {"id": sid, "actions": []}
            result["scenarios"].append(sresult)
            for index, step in enumerate(scenario["steps"]):
                kind = step["kind"]
                action = {"id": step.get("id", f"{index:02d}"), "kind": kind}
                if kind == "skip":
                    action.update(status="skipped", reason=step.get("reason", "not supported by this setup"))
                    sresult["actions"].append(action)
                    result["summary"]["skipped"] += 1
                    continue
                if kind == "assert_gump":
                    gump = step["gump"]
                    visible = {}
                    for name, mcp in clients.items():
                        ui = mcp.call_json("guo_ui")
                        visible[name] = any(c.get("system") == "classic" and c.get("gump") == gump for c in ui.get("controls", []))
                    expected = step.get("visible", True)
                    action["assertions"] = {name: {"expected": expected, "actual": value, "passed": value == expected}
                                             for name, value in visible.items()}
                    if all(value == expected for value in visible.values()):
                        action["status"] = "asserted"
                        result["summary"]["asserted"] += 1
                    else:
                        action["status"] = "failed"
                        result["summary"]["failed"] += 1
                        sresult["actions"].append(action)
                        (Path(args.out) / "actions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
                        raise McpError(f"{sid}: {gump} visibility assertion failed")
                    sresult["actions"].append(action)
                    continue
                if kind == "if_button":
                    roots = {}
                    missing = []
                    for name, mcp in clients.items():
                        ui = mcp.call_json("guo_ui")
                        button = next((c for c in ui.get("controls", [])
                                       if c.get("system") == "classic" and c.get("gump") == step["gump"]
                                       and c.get("type") == "Button" and c.get("buttonId") == step["button_id"]), None)
                        if button is None:
                            missing.append(name)
                        else:
                            roots[name] = button
                    if missing:
                        action.update(status="skipped", skipped_clients=missing,
                                      reason=f"button {step['button_id']} in {step['gump']} was not visible in both clients")
                        sresult["actions"].append(action)
                        result["summary"]["skipped"] += 1
                        continue
                    for name, mcp in clients.items():
                        button = roots[name]
                        payload = {"kind": "click", "button": "Left", "clicks": 1,
                                   "x": int(button["x"] + button["width"] / 2),
                                   "y": int(button["y"] + button["height"] / 2)}
                        failed, reply = mcp.call("guo_input", payload)
                        if failed: raise McpError(f"{sid}: button click failed: {reply[:200]}")
                    if step.get("expect_gump"):
                        for mcp in clients.values():
                            mcp.call("guo_wait", {"frames": step.get("wait_frames", 30)})
                        expected_gump = step["expect_gump"]
                        visible = {name: any(c.get("system") == "classic" and c.get("gump") == expected_gump
                                             for c in mcp.call_json("guo_ui").get("controls", []))
                                   for name, mcp in clients.items()}
                        action["assertions"] = {name: {"expected": True, "actual": value, "passed": value}
                                                 for name, value in visible.items()}
                        if not all(visible.values()):
                            action["status"] = "failed"
                            result["summary"]["failed"] += 1
                            sresult["actions"].append(action)
                            (Path(args.out) / "actions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
                            raise McpError(f"{sid}: click did not open {expected_gump}")
                        action["status"] = "asserted"
                        result["summary"]["asserted"] += 1
                    else:
                        action.update(status="executed_unasserted")
                        result["summary"]["executed_unasserted"] += 1
                    sresult["actions"].append(action)
                    continue
                if kind == "if_gump":
                    event = step["event"]
                    roots = {}
                    missing = []
                    for name, mcp in clients.items():
                        ui = mcp.call_json("guo_ui")
                        gump = step["gump"]
                        root = next((c for c in ui.get("controls", []) if c.get("system") == "classic" and c.get("type") == gump and c.get("gump") == gump), None)
                        if root is None:
                            missing.append(name)
                        else:
                            roots[name] = root
                    if missing:
                        action.update(status="skipped", skipped_clients=missing, reason=f"{step['gump']} was not visible in both clients")
                        sresult["actions"].append(action)
                        result["summary"]["skipped"] += 1
                        continue
                    for name, mcp in clients.items():
                        root = roots[name]
                        x, y, w, h = (int(root[k]) for k in ("x", "y", "width", "height"))
                        if event == "wheel":
                            for button in ("WheelUp", "WheelDown"):
                                failed, reply = mcp.call("guo_input", {"kind": "button", "button": button, "pressed": True, "x": x + w // 2, "y": y + h // 2})
                                if failed: raise McpError(f"{sid}: {button} failed: {reply[:200]}")
                        elif event == "hover":
                            mcp.call("guo_input", {"kind": "motion", "x": x + w // 2, "y": y + h // 2})
                        elif event == "drag":
                            start = (x + max(12, w // 2), y + min(12, h // 2))
                            end = (start[0] + 24, start[1] + 24)
                            for payload in ({"kind": "motion", "x": start[0], "y": start[1]},
                                            {"kind": "button", "button": "Left", "pressed": True, "x": start[0], "y": start[1]},
                                            {"kind": "motion", "x": end[0], "y": end[1]},
                                            {"kind": "button", "button": "Left", "pressed": False, "x": end[0], "y": end[1]}):
                                failed, reply = mcp.call("guo_input", payload)
                                if failed: raise McpError(f"{sid}: gump drag failed: {reply[:200]}")
                        else:
                            raise McpError(f"{sid}: unsupported conditional gump action {event!r}")
                    action.update(status="executed_unasserted", reason=f"{event} sent; see checkpoint image and UI bounds")
                    result["summary"]["executed_unasserted"] += 1
                    sresult["actions"].append(action)
                    continue
                if kind == "capture":
                    capture(argparse.Namespace(out=str(Path(args.out) / sid / f"{index:02d}-{step.get('name','capture')}"), settle_frames=step.get("frames", 8)), clients)
                    action.update(status="executed_unasserted", reason="visual checkpoint saved; image comparison is not an automatic pass")
                    sresult["actions"].append(action)
                    result["summary"]["executed_unasserted"] += 1
                    continue
                for mcp in clients.values():
                    if kind == "wait":
                        failed, reply = mcp.call("guo_wait", {"frames": step.get("frames", 8)})
                    elif kind == "chat":
                        for args_ in ({"kind": "key", "key": "Enter", "pressed": True}, {"kind": "key", "key": "Enter", "pressed": False},
                                      {"kind": "text", "text": step["text"]}, {"kind": "key", "key": "Enter", "pressed": True},
                                      {"kind": "key", "key": "Enter", "pressed": False}):
                            failed, reply = mcp.call("guo_input", args_)
                            if failed: raise McpError(f"{sid}: chat input failed: {reply[:200]}")
                        continue
                    elif kind == "input":
                        event = step["event"]
                        fields = {"key": {"key", "pressed", "shift", "ctrl", "alt"},
                                  "text": {"text"},
                                  "motion": {"x", "y", "shift", "ctrl", "alt", "position_ratio"},
                                  "button": {"button", "pressed", "x", "y", "shift", "ctrl", "alt"},
                                  "click": {"button", "clicks", "x", "y"}}
                        if event not in fields:
                            raise McpError(f"{sid}: unsupported input event {event!r}")
                        payload = {k: v for k, v in step.items() if k in fields[event] and k != "position_ratio"}
                        if "position_ratio" in step and event == "motion":
                            ratio = step["position_ratio"]
                            state = mcp.call_json("guo_state")
                            payload["x"] = int(state["width"] * ratio[0])
                            payload["y"] = int(state["height"] * ratio[1])
                        payload["kind"] = event
                        failed, reply = mcp.call("guo_input", payload)
                    else:
                        raise McpError(f"{sid}: unsupported step kind {kind!r}")
                    if failed:
                        raise McpError(f"{sid}: {kind} failed: {reply[:200]}")
                expected = step.get("expect")
                if expected is None:
                    action.update(status="executed_unasserted")
                    result["summary"]["executed_unasserted"] += 1
                else:
                    states = {name: mcp.call_json("guo_state") for name, mcp in clients.items()}
                    checks = {}
                    for name, state in states.items():
                        checks[name] = {}
                        for path, wanted in expected.items():
                            actual = state.get("player") is not None if path == "player.exists" else state
                            if path != "player.exists":
                                for part in path.split("."):
                                    actual = actual.get(part) if isinstance(actual, dict) else None
                            checks[name][path] = {"expected": wanted, "actual": actual, "passed": actual == wanted}
                    action["assertions"] = checks
                    if all(v["passed"] for by_client in checks.values() for v in by_client.values()):
                        action["status"] = "asserted"
                        result["summary"]["asserted"] += 1
                    else:
                        action["status"] = "failed"
                        result["summary"]["failed"] += 1
                        sresult["actions"].append(action)
                        (Path(args.out) / "actions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
                        raise McpError(f"{sid}: assertion failed at {action['id']}")
                sresult["actions"].append(action)
            print(f"completed scenario {sid}")
        (Path(args.out) / "actions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        return 0
    except Exception as exc:
        done = {(scenario["id"], action["id"]) for scenario in result["scenarios"] for action in scenario["actions"]}
        for scenario in plan["scenarios"]:
            row = next((r for r in result["scenarios"] if r["id"] == scenario["id"]), None)
            if row is None:
                row = {"id": scenario["id"], "actions": []}
                result["scenarios"].append(row)
            for index, step in enumerate(scenario["steps"]):
                action_id = step.get("id", f"{index:02d}")
                if (scenario["id"], action_id) not in done:
                    row["actions"].append({"id": action_id, "kind": step["kind"], "status": "skipped",
                                           "reason": f"prior action stopped the run: {type(exc).__name__}"})
                    result["summary"]["skipped"] += 1
        (Path(args.out) / "actions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        raise
    finally:
        for mcp in clients.values():
            mcp.close()


def session(args: argparse.Namespace) -> int:
    """Launch an isolated CUO/GUO pair or attach to MCP endpoints and replay the action plan."""
    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.attach:
        return drive(argparse.Namespace(plan=args.plan, out=str(out)))

    cfg = load_config()
    if not cfg.shard_gm_accounts:
        raise McpError("No second account is configured; set UO_SHARD_GM_ACCOUNTS and UO_SHARD_GM_PASSWORD.")
    guo_account = args.guo_account or cfg.shard_gm_accounts[0]
    if not cfg.shard_gm_password:
        raise McpError("UO_SHARD_GM_PASSWORD is missing; configure the separate GUO account before launching.")

    from dataclasses import replace
    from cuo_automation import run as cuo_runner
    scenario_session = _load("guo_visual_scenario_session", TOOLS / "scenario_run" / "session.py")
    width, height = parse_size(args.size)
    logs = out / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    cuo = guo_session = guo_mcp = None
    scratch = tempfile.TemporaryDirectory(prefix="client-state-", dir=out)
    old_env = {key: os.environ.get(key) for key in ("CUO_MCP_PORT", "CUO_MCP_TOKEN", "GUO_MCP_PORT", "GUO_MCP_TOKEN")}
    try:
        cuo_exe = Path(args.cuo_build).resolve() / "cuo.exe" if args.cuo_build else cfg.upstream_exe
        if not cuo_exe.is_file():
            if args.cuo_build:
                raise McpError(f"Pinned CUO executable missing: {cuo_exe}")
            cuo_runner.ab_compare().build_cuo(cfg)
        cuo_account = args.cuo_account or cfg.shard_owner
        if cuo_account.casefold() == cfg.shard_owner.casefold():
            cuo_password = cfg.shard_owner_password
        elif cuo_account.casefold() in {name.casefold() for name in cfg.shard_gm_accounts}:
            cuo_password = cfg.shard_gm_password
        else:
            raise McpError("--cuo-account must be the configured owner account or one of UO_SHARD_GM_ACCOUNTS.")
        if cuo_account.casefold() == guo_account.casefold():
            raise McpError("CUO and GUO must use separate shard accounts.")
        cuo_config = replace(cfg, shard_host=args.shard_host or cfg.shard_host,
                             shard_port=args.shard_port or cfg.shard_port,
                             shard_owner=cuo_account, shard_owner_password=cuo_password)
        if not cuo_config.shard_owner_password:
            raise McpError("UO_SHARD_OWNER_PASSWORD is missing; configure the CUO account before launching.")
        cuo_character = args.cuo_character or cuo_runner.ab_compare().last_played(cuo_config) or ""
        if not cuo_character:
            raise McpError("No CUO character was found in the saved GUO profiles; pass --cuo-character.")
        cuo = cuo_runner.start_client(
            cuo_config, logs / "cuo", (width, height), tuck=True,
            extra_env={"CUO_AUTOMATION_CHARACTER": cuo_character}, executable=cuo_exe,
            config_dir=Path(scratch.name) / "cuo",
        )

        (logs / "guo").mkdir(parents=True, exist_ok=True)
        guo_session = scenario_session.ClientSession(cfg, logs / "guo", record=False, size=f"{width}x{height}")
        guo_session.home = Path(scratch.name) / "guo"
        guo_session.home.mkdir()
        (guo_session.home / "settings.json").write_text(
            json.dumps({"run_mouse_in_separate_thread": False}), encoding="utf-8",
        )
        run_build = Path(scratch.name) / "guo-build"
        run_build.mkdir(parents=True, exist_ok=True)
        guo_session.extra_env["UO_BUILD_DIR"] = str(run_build)
        if args.shard_host is not None or args.shard_port is not None:
            guo_session.shard = (args.shard_host or cfg.shard_host, args.shard_port or cfg.shard_port)
        guo_session.extra_args = ["--play", "--account", guo_account, "--password", cfg.shard_gm_password,
                                  "--autologin", "--stay", "--pregame-classic", "--postfx", "off"]
        if args.guo_character:
            guo_session.extra_args.extend(["--character", args.guo_character])
        guo_session.start()
        if not guo_session.wait_listening(args.login_timeout):
            raise McpError("GUO MCP did not start; inspect the run's logs/guo/client.log")
        guo_mcp = McpClient(guo_session.port, guo_session.token)

        cuo.wait_player(args.login_timeout)
        deadline = time.monotonic() + args.login_timeout
        while time.monotonic() < deadline:
            state = guo_mcp.call_json("guo_state")
            if state.get("player") is not None:
                break
            if not guo_session.alive():
                raise McpError("GUO exited before entering the world; inspect the run's logs/guo/client.log")
            time.sleep(1)
        else:
            raise McpError("GUO did not enter the world before timeout")

        os.environ.update({"CUO_MCP_PORT": str(cuo.port), "CUO_MCP_TOKEN": cuo.token,
                           "GUO_MCP_PORT": str(guo_session.port), "GUO_MCP_TOKEN": guo_session.token})
        status = drive(argparse.Namespace(plan=args.plan, out=str(out)))
        (out / "session.json").write_text(json.dumps({"status": "complete", "clients": {"cuo": "launched-no-focus", "guo": "launched-no-focus"},
                                                        "asserted_actions": json.loads((out / "actions.json").read_text(encoding="utf-8"))["summary"]["asserted"]}, indent=2), encoding="utf-8")
        return status
    except Exception as exc:
        plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        if not (out / "actions.json").exists():
            skipped = [{"id": scenario["id"], "actions": [
                {"id": step.get("id", f"{index:02d}"), "kind": step["kind"], "status": "skipped", "reason": f"session stopped before actions: {type(exc).__name__}"}
                for index, step in enumerate(scenario["steps"])]} for scenario in plan.get("scenarios", [])]
            (out / "actions.json").write_text(json.dumps({"manifest": str(Path(args.plan).resolve()), "scenarios": skipped,
                                                           "summary": {"asserted": 0, "executed_unasserted": 0,
                                                                       "skipped": sum(len(r["actions"]) for r in skipped), "failed": 0}}, indent=2), encoding="utf-8")
        (out / "session.json").write_text(json.dumps({"status": "blocked", "error_type": type(exc).__name__,
                                                       "reason": "See client logs; the pair did not complete the action manifest."}, indent=2), encoding="utf-8")
        raise
    finally:
        if guo_mcp is not None:
            guo_mcp.close()
        if guo_session is not None:
            try:
                mcp = McpClient(guo_session.port, guo_session.token)
                mcp.call("guo_quit")
                mcp.close()
                guo_session.wait_exit(30)
            except Exception:
                pass
            guo_session.stop()
        if cuo is not None:
            cuo.quit()
        if guo_session is not None:
            cuo_runner.scrub(guo_session.log_path, [cfg.shard_gm_password, guo_session.token])
        scratch.cleanup()
        for key, value in old_env.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="command", required=True)
    p = sub.add_parser("capture", help="capture and pair visible gumps from running MCP clients")
    p.add_argument("--out", required=True, help="external evidence folder for PNGs and JSON")
    p.add_argument("--settle-frames", type=int, default=8)
    p.set_defaults(fn=capture)
    a = sub.add_parser("gump-ids", help="batch export gump IDs via the built pinned-loader guoasset MCP")
    a.add_argument("--start", type=lambda v: int(v, 0), required=True, help="first ID, decimal or 0x hex")
    a.add_argument("--end", type=lambda v: int(v, 0), required=True, help="last ID, inclusive")
    a.add_argument("--out", required=True, help="external folder; exported art is proprietary client data")
    a.add_argument("--server", help="optional path to guoasset.dll (default under configured UO_BUILD_DIR)")
    a.set_defaults(fn=asset_export)
    d = sub.add_parser("drive", help="replay a deterministic JSON input plan on both running clients")
    d.add_argument("--plan", required=True, help="JSON scenarios with wait/chat/input/capture steps")
    d.add_argument("--out", required=True, help="external evidence root")
    d.set_defaults(fn=drive)
    s = sub.add_parser("session", help="launch no-focus CUO/GUO sessions and replay the shared plan")
    s.add_argument("--plan", default=str(HERE / "matrix.plan.json"))
    s.add_argument("--out", required=True, help="external evidence folder")
    s.add_argument("--attach", action="store_true", help="use running MCP clients instead of launching clients")
    s.add_argument("--size", default="1024x768")
    s.add_argument("--cuo-build", help="CUO folder containing cuo.exe (for a pinned reference build)")
    s.add_argument("--shard-host", help="override UO_SHARD_HOST for both clients")
    s.add_argument("--shard-port", type=int, help="override UO_SHARD_PORT for both clients")
    s.add_argument("--cuo-account")
    s.add_argument("--cuo-character")
    s.add_argument("--guo-account")
    s.add_argument("--guo-character", help="select an existing GUO character")
    s.add_argument("--login-timeout", type=float, default=150)
    s.set_defaults(fn=session)
    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (McpError, OSError, ValueError) as exc:
        print(f"visual parity: {exc}", file=sys.stderr)
        raise SystemExit(2)
