#!/usr/bin/env python3
"""A private ModernUO instance for the editor's live and export work.

The dev shard (launchers\\shard) is shared: other agents and devices play on
it. The editor's phase 4 work (live patching, GM commands, a shard reading a
world export) runs on an instance of its own instead:

    python tools/editor_shard/run.py setup   [--from DIR] [--port 2594]
    python tools/editor_shard/run.py start   [--data-first DIR]
    python tools/editor_shard/run.py status
    python tools/editor_shard/run.py stop
    python tools/editor_shard/run.py bridge  [--bridge-port 2595]

setup copies the built ModernUO Distribution (the same tools/modernuo build,
read only; Archives, Backups, Logs left out) to build\\shard_private, with its
own Saves snapshot, and rewrites the copy's Configuration\\modernuo.json:
listener 127.0.0.1:<port> (default 2594) and nothing wider, data directories
the install. The shared shard's files are only ever read.

start runs the copy's ModernUO.exe in the background, logging to
build\\shard_private\\shard.log, and waits until it listens. --data-first puts
a folder (a tools/world export) ahead of the install in dataDirectories for
this start; a start without it puts the install back alone.

bridge builds tools/editor_shard/bridge (GUO.EditorBridge.dll, a ModernUO
assembly: UltimaLive for game clients, a JSON line protocol for editors on
127.0.0.1:<bridge-port>) against the copy's own Server.dll and lists it in the
copy's Data/assemblies.json. It takes effect at the next start.

stop ends only the process start recorded, and only if its executable is the
copy's: it cannot stop the shared shard.

Clients reach it through environment variables for that run only
(UO_SHARD_HOST=127.0.0.1, UO_SHARD_PORT=<port>); config.bat is not changed.
The copied Saves include the dev shard's accounts, so the GM lane accounts
log in here as they do there.

Exit codes: 0 ok, 1 failed, 2 bad state (not set up, already running, ...).
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

LEAVE_OUT = {"Archives", "Backups", "Logs", "temp"}


# Every facet is offered to UltimaLive clients: a UOP-only client converts
# its own map copies from the install (ADR-0012), so no facet goes blank.
ALL_FACETS = "0,1,2,3,4,5"


# The published server: an apphost named ModernUO.exe on Windows, ModernUO elsewhere.
EXE = "ModernUO.exe" if sys.platform == "win32" else "ModernUO"


def home(cfg) -> Path:
    return cfg.build / "shard_private"


def default_source(cfg) -> Path:
    """The built Distribution: this checkout's, or the main worktree's (worktrees share the build)."""
    if (cfg.shard_dist / EXE).exists():
        return cfg.shard_dist
    common = subprocess.run(["git", "-C", str(cfg.root), "rev-parse", "--git-common-dir"],
                            capture_output=True, text=True).stdout.strip()
    main_root = Path(common).resolve().parent if common else cfg.root
    return main_root / "tools" / "modernuo" / "src" / "Distribution"


def listening(port: int) -> bool:
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0


def read_state(h: Path) -> dict:
    f = h / "state.json"
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {}


def write_state(h: Path, state: dict) -> None:
    (h / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")


def pid_alive(pid: int, exe: Path) -> bool:
    """True if pid is running and is the copy's ModernUO."""
    if sys.platform != "win32":
        try:
            return Path(os.readlink(f"/proc/{pid}/exe")).resolve() == exe.resolve()
        except OSError:
            return False
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          f"(Get-Process -Id {pid} -ErrorAction SilentlyContinue).Path"],
                         capture_output=True, text=True).stdout.strip()
    return bool(out) and Path(out).resolve() == exe.resolve()


def configure(h: Path, cfg, port: int, data_first: Path | None) -> None:
    conf = h / "Configuration" / "modernuo.json"
    j = json.loads(conf.read_text(encoding="utf-8"))
    j["listeners"] = [f"127.0.0.1:{port}"]
    dirs = [str(cfg.client_data)]
    if data_first is not None:
        dirs.insert(0, str(data_first))
    j["dataDirectories"] = dirs
    conf.write_text(json.dumps(j, indent=2), encoding="utf-8")


def cmd_setup(cfg, source: Path, port: int) -> int:
    h = home(cfg)
    if (h / EXE).exists():
        print(f"[editor_shard] already set up: {h} (delete it to start over)")
        return 2
    if not (source / EXE).exists():
        print(f"[editor_shard] no built ModernUO at {source}; build the dev shard first (launchers/shard/build.bat or build.sh)")
        return 1
    print(f"[editor_shard] copying {source} -> {h} (without {', '.join(sorted(LEAVE_OUT))})")
    shutil.copytree(source, h, ignore=lambda d, names: [n for n in names if Path(d) == source and n in LEAVE_OUT])
    configure(h, cfg, port, None)
    write_state(h, {"port": port, "source": str(source)})
    print(f"[editor_shard] ready: 127.0.0.1:{port}")
    return 0


def install_objects(h: Path, export: Path) -> bool:
    """Puts a tools/world export's world objects (ADR-0014) where the bridge syncs them from at boot."""
    src = export / "shard"
    manifest = src / "guo_objects.json"
    if not manifest.is_file():
        print(f"[editor_shard] {export} has no world objects (shard/guo_objects.json)")
        return False
    for f in (src / "Data").rglob("*"):
        if f.is_file():
            dst = h / "Data" / f.relative_to(src / "Data")
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(f, dst)
    (h / "Data" / "GUO").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(manifest, h / "Data" / "GUO" / "guo_objects.json")
    print(f"[editor_shard] world objects from {export} installed; the bridge syncs them at boot")
    return True


def clear_objects(h: Path) -> None:
    """An empty manifest: the bridge's boot sync then deletes every object GUO placed (and nothing else)."""
    (h / "Data" / "GUO").mkdir(parents=True, exist_ok=True)
    (h / "Data" / "GUO" / "guo_objects.json").write_text(
        json.dumps({"format": 1, "backend": "modernuo", "project": None, "spawners": [], "items": [], "files": []}) + "\n",
        encoding="utf-8")
    print("[editor_shard] empty world-objects manifest installed; the bridge removes GUO's objects at boot")


def set_bridge_listed(h: Path, listed: bool) -> None:
    """Lists or unlists the bridge assembly in the copy's Data/assemblies.json (the DLL stays installed)."""
    path = h / "Data" / "assemblies.json"
    names = json.loads(path.read_text(encoding="utf-8"))
    name = "GUO.EditorBridge.dll"
    if listed and name not in names and (h / "Assemblies" / name).exists():
        names.append(name)
    elif not listed:
        names = [n for n in names if n != name]
    path.write_text(json.dumps(names, indent=2), encoding="utf-8")


def cmd_start(cfg, data_first: Path | None, objects: Path | None = None, clear: bool = False,
              no_bridge: bool = False) -> int:
    h = home(cfg)
    state = read_state(h)
    if not state:
        print("[editor_shard] not set up; run: python tools/editor_shard/run.py setup")
        return 2
    set_bridge_listed(h, not no_bridge)
    if no_bridge:
        print("[editor_shard] starting WITHOUT the bridge: a plain ModernUO, no GUO code on the server")
    if clear:
        clear_objects(h)
    elif objects is not None and not install_objects(h, objects.resolve()):
        return 2
    exe = h / EXE
    if state.get("pid") and pid_alive(state["pid"], exe):
        print(f"[editor_shard] already running (pid {state['pid']})")
        return 2
    port = state["port"]
    if listening(port):
        print(f"[editor_shard] something else already listens on 127.0.0.1:{port}")
        return 2

    configure(h, cfg, port, data_first.resolve() if data_first else None)
    log = (h / "shard.log").open("w", encoding="utf-8", errors="replace")
    env = {**__import__("os").environ,
           "GUO_BRIDGE_PORT": str(state.get("bridge_port", 2595)),
           "GUO_BRIDGE_SHARD": __import__("os").environ.get("GUO_BRIDGE_SHARD") or state.get("bridge_shard", "GUO-Editor-Private"),
           "GUO_BRIDGE_MAPS": state.get("bridge_maps", ALL_FACETS)}
    proc = subprocess.Popen([str(exe)], cwd=str(h), env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    state.update({"pid": proc.pid, "data_first": str(data_first.resolve()) if data_first else None,
                  "started": time.strftime("%Y-%m-%d %H:%M:%S")})
    write_state(h, state)
    for _ in range(180):
        if listening(port):
            print(f"[editor_shard] up: pid {proc.pid}, 127.0.0.1:{port}, data {'export first' if data_first else 'install'}")
            return 0
        if proc.poll() is not None:
            print(f"[editor_shard] exited {proc.returncode} while starting; see {h / 'shard.log'}")
            return 1
        time.sleep(1)
    print(f"[editor_shard] not listening after 180 s; see {h / 'shard.log'}")
    return 1


def cmd_status(cfg) -> int:
    h = home(cfg)
    state = read_state(h)
    if not state:
        print("[editor_shard] not set up")
        return 2
    alive = bool(state.get("pid")) and pid_alive(state["pid"], h / EXE)
    conf = json.loads((h / "Configuration" / "modernuo.json").read_text(encoding="utf-8"))
    print(f"[editor_shard] {h}")
    print(f"[editor_shard] {'running, pid ' + str(state['pid']) if alive else 'stopped'}; "
          f"listens {conf['listeners']}; data {conf['dataDirectories']}")
    return 0


def cmd_bridge(cfg, bridge_port: int) -> int:
    h = home(cfg)
    state = read_state(h)
    if not state:
        print("[editor_shard] not set up; run: python tools/editor_shard/run.py setup")
        return 2
    if state.get("pid") and pid_alive(state["pid"], h / EXE):
        print("[editor_shard] stop the private shard first; its Assemblies are in use")
        return 2
    proj = Path(__file__).resolve().parent / "bridge" / "GUO.EditorBridge.csproj"
    r = subprocess.run(["dotnet", "build", str(proj), "-nologo", "-v", "q", f"-p:ModernUODir={h}"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-3000:])
        return 1
    dll = cfg.build / "editor_bridge" / "GUO.EditorBridge.dll"
    shutil.copy2(dll, h / "Assemblies" / dll.name)
    listed = h / "Data" / "assemblies.json"
    names = json.loads(listed.read_text(encoding="utf-8"))
    if dll.name not in names:
        names.append(dll.name)
        listed.write_text(json.dumps(names, indent=2), encoding="utf-8")
    state["bridge_port"] = bridge_port
    write_state(h, state)
    print(f"[editor_shard] bridge installed in the private copy (editors on 127.0.0.1:{bridge_port}); restart to load it")
    return 0


def cmd_stop(cfg) -> int:
    h = home(cfg)
    state = read_state(h)
    pid = state.get("pid")
    exe = h / EXE
    if not pid or not pid_alive(pid, exe):
        print("[editor_shard] not running")
        return 0
    # Only this copy's process, by the pid it was started with.
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    else:
        os.kill(pid, signal.SIGTERM)
    for _ in range(30):
        if not pid_alive(pid, exe):
            break
        time.sleep(0.5)
    state["pid"] = None
    write_state(h, state)
    print(f"[editor_shard] stopped pid {pid}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["setup", "start", "status", "stop", "bridge"])
    ap.add_argument("--from", dest="source", type=Path, help="built ModernUO Distribution to copy (setup)")
    ap.add_argument("--port", type=int, default=2594, help="port for the private instance (setup)")
    ap.add_argument("--data-first", type=Path, help="folder ahead of the install in dataDirectories (start)")
    ap.add_argument("--objects", type=Path,
                    help="a tools/world export whose world objects the bridge syncs at boot (start; needs the bridge)")
    ap.add_argument("--no-bridge", action="store_true",
                    help="start as a plain ModernUO, the bridge assembly unlisted for this run (start)")
    ap.add_argument("--clear-objects", action="store_true",
                    help="remove every world object GUO placed, at boot (start; needs the bridge)")
    ap.add_argument("--bridge-port", type=int, default=2595, help="editor port of the bridge (bridge)")
    args = ap.parse_args()
    cfg = load_config()
    if args.command == "setup":
        return cmd_setup(cfg, (args.source or default_source(cfg)).resolve(), args.port)
    if args.command == "start":
        return cmd_start(cfg, args.data_first, args.objects, args.clear_objects, args.no_bridge)
    if args.command == "status":
        return cmd_status(cfg)
    if args.command == "bridge":
        return cmd_bridge(cfg, args.bridge_port)
    return cmd_stop(cfg)


if __name__ == "__main__":
    sys.exit(main())
