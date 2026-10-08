#!/usr/bin/env python3
"""A private ServUO shard for the editor's world-objects backend (ADR-0014).

    python tools/servuo/run.py fetch       shallow-fetch the pinned commit into tools/servuo/src
    python tools/servuo/run.py build       dotnet build (net48; Windows has the runtime)
    python tools/servuo/run.py configure   write our settings into src/Config (port, data path, accounts)
    python tools/servuo/run.py start       run it in the background; answers the first-boot prompt
    python tools/servuo/run.py status
    python tools/servuo/run.py stop

ServUO is GPL-3.0 and is never vendored: src/ is gitignored, and nothing here
is copied from it. What is committed is ours: this tool, config/*.cfg (only
the key=value lines we set, applied over ServUO's own files line by line) and
the README. It listens on 127.0.0.1:2596 only, beside the private ModernUO
instance (2594) and its editor bridge (2595); the shared dev shard (2593) is
never touched.

First boot: ServUO asks on its console whether to create an owner account.
start answers from config (UO_SHARD_OWNER, and the password from
UO_SHARD_OWNER_PASSWORD), so the scripted GM client can log in; the password
is never written to a file by this tool.

Exit codes: 0 ok, 1 failed, 2 bad state.
"""
from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

REPO = "https://github.com/ServUO/ServUO.git"
BRANCH = "pub57"
PIN = "d76bf4443cf76d081ddaf8f57c87ff33749256af"
PORT = 2596
HERE = Path(__file__).resolve().parent
SRC = HERE / "src"
STATE = SRC / "guo_state.json"


def listening(port: int) -> bool:
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0


def pid_alive(pid: int) -> bool:
    r = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"], capture_output=True, text=True)
    return "ServUO.exe" in r.stdout


def cmd_fetch() -> int:
    if (SRC / ".git").exists():
        head = subprocess.run(["git", "-C", str(SRC), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        print(f"[servuo] already fetched at {head[:9]} (pin {PIN[:9]})")
        return 0 if head == PIN else 1
    subprocess.run(["git", "init", "-q", str(SRC)], check=True)
    subprocess.run(["git", "-C", str(SRC), "remote", "add", "origin", REPO], check=True)
    if subprocess.run(["git", "-C", str(SRC), "fetch", "-q", "--depth", "1", "origin", PIN]).returncode != 0:
        return 1
    subprocess.run(["git", "-C", str(SRC), "checkout", "-q", "FETCH_HEAD"], check=True)
    print(f"[servuo] fetched {REPO} {BRANCH} at {PIN[:9]} -> {SRC}")
    return 0


def cmd_build() -> int:
    r = subprocess.run(["dotnet", "build", str(SRC / "ServUO.sln"), "-c", "Release", "-nologo", "-v", "q"])
    ok = r.returncode == 0 and (SRC / "ServUO.exe").exists()
    print("[servuo] built ServUO.exe" if ok else "[servuo] build FAILED")
    return 0 if ok else 1


def cmd_configure(cfg) -> int:
    """Our key=value lines over ServUO's own config files, line by line."""
    values = {"CLIENT_DATA": str(cfg.client_data), "PORT": str(PORT)}
    for template in sorted((HERE / "config").glob("*.cfg")):
        target = SRC / "Config" / template.name
        ours = {}
        for line in template.read_text(encoding="utf-8").splitlines():
            if line.strip() and not line.startswith("#"):
                k, v = line.split("=", 1)
                ours[k.strip()] = v.strip().format(**values)
        lines = target.read_text(encoding="utf-8", errors="replace").splitlines() if target.exists() else []
        seen = set()
        out = []
        for line in lines:
            key = line.lstrip("#").split("=", 1)[0].strip() if "=" in line else None
            if key in ours and key not in seen:
                out.append(f"{key}={ours[key]}")
                seen.add(key)
            else:
                out.append(line)
        out += [f"{k}={v}" for k, v in ours.items() if k not in seen]
        target.write_text("\n".join(out) + "\n", encoding="utf-8")
        print(f"[servuo] {template.name}: {', '.join(sorted(ours))}")
    return 0


def read_state() -> dict:
    return json.loads(STATE.read_text(encoding="utf-8")) if STATE.exists() else {}


def cmd_start(cfg) -> int:
    state = read_state()
    if state.get("pid") and pid_alive(state["pid"]):
        print(f"[servuo] already running (pid {state['pid']})")
        return 2
    if listening(PORT):
        print(f"[servuo] something already listens on 127.0.0.1:{PORT}")
        return 2
    first = not (SRC / "Saves" / "Accounts").exists()
    if first:
        owner = os.environ.get("UO_SHARD_OWNER") or cfg_value(cfg, "UO_SHARD_OWNER") or "guoprobe"
        # The dev shard's generated owner password (tools/guo/shard_secrets.py); no default.
        password = cfg.shard_owner_password
        if not password:
            print("[servuo] no owner password: run launchers/shard/run.bat once to generate it, "
                  "or set UO_SHARD_OWNER_PASSWORD")
            return 2
    log = (SRC / "guo_shard.log").open("w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen([str(SRC / "ServUO.exe")], cwd=str(SRC), stdin=subprocess.PIPE, stdout=log,
                            stderr=subprocess.STDOUT, creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    if first:
        proc.stdin.write(f"y\n{owner}\n{password}\n".encode())
        proc.stdin.flush()
        print(f"[servuo] first boot: creating owner account '{owner}'")
    STATE.write_text(json.dumps({"pid": proc.pid, "port": PORT, "started": time.strftime("%Y-%m-%d %H:%M:%S")}), encoding="utf-8")
    for _ in range(600):
        if listening(PORT):
            print(f"[servuo] up: pid {proc.pid}, 127.0.0.1:{PORT}")
            return 0
        if proc.poll() is not None:
            break
        time.sleep(0.5)
    print(f"[servuo] did not come up; see {SRC / 'guo_shard.log'}")
    return 1


def cfg_value(cfg, key: str) -> str | None:
    from guo.config import parse_config_bat
    shared = cfg.root / "launchers" / "_shared"
    values = parse_config_bat(shared / "config.bat")
    local = shared / "config.local.bat"
    if local.is_file():
        values.update(parse_config_bat(local))
    return values.get(key)


def cmd_stop() -> int:
    state = read_state()
    pid = state.get("pid")
    if not pid or not pid_alive(pid):
        print("[servuo] not running")
        return 0
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    state["pid"] = None
    STATE.write_text(json.dumps(state), encoding="utf-8")
    print(f"[servuo] stopped pid {pid}")
    return 0


def cmd_status() -> int:
    state = read_state()
    up = bool(state.get("pid")) and pid_alive(state["pid"])
    print(f"[servuo] {'running pid ' + str(state['pid']) if up else 'stopped'}; 127.0.0.1:{PORT}; "
          f"listening {listening(PORT)}; fetched {(SRC / '.git').exists()}; built {(SRC / 'ServUO.exe').exists()}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["fetch", "build", "configure", "start", "stop", "status"])
    args = ap.parse_args()
    cfg = load_config()
    return {"fetch": cmd_fetch, "build": cmd_build, "stop": cmd_stop, "status": cmd_status}.get(
        args.command, lambda: {"configure": cmd_configure, "start": cmd_start}[args.command](cfg))()


if __name__ == "__main__":
    sys.exit(main())
