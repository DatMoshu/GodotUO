r"""Load a native and a managed test plugin into GUO and check what they saw.

    launchers\dev\plugin_probe.bat

Assistants are what most shards expect players to run, and there are two ways
into the client for one. A native DLL exports Install and is called directly.
A managed .NET Framework assembly (Razor, Razor Enhanced, ClassicAssist) has
no export. It goes through the ported bootstrap in tools\plugin_host, which
GUO starts inside its own process. This builds one of each, plays a session
against the configured shard with both listed, and reads their logs back.

Each plugin must have been installed, initialised and told of the connection.
It must have seen packets arrive and leave, and been told the player's
position. When it asked the client for that position, the answer must match
what it was told. The managed probe also grows one ping each way once in the
world, as Razor's filters can; the host must drop those with a warning and
the session must carry on (the player keeps moving afterwards). Exits 0 when
both pass.

WHAT IT TOUCHES

The client runs with its home in build\plugin_probe\home, via --cache-dir.
That home gets its own settings.json listing the two probes, copied from the
real one with the account fields blanked. The real settings.json is never
edited. The plugins log to build\plugin_probe\logs.

Needs the dev shard running (launchers\shard\run.bat) and MSVC for the native
probe, found through vswhere or, failing that, the standard Visual Studio
folders under Program Files.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, load_config  # noqa: E402

HERE = Path(__file__).resolve().parent


def find_vcvars() -> Path:
    program_files = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    vswhere = program_files / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"

    if not vswhere.exists():
        sys.exit("[plugin_probe] vswhere not found; the native probe needs MSVC")

    # Newest first. -prerelease because a preview Visual Studio is still a
    # working compiler; the check is simply whether vcvars64.bat is there.
    installs = subprocess.run(
        [str(vswhere), "-all", "-prerelease", "-products", "*",
         "-sort", "-property", "installationPath"],
        capture_output=True, text=True, check=True,
    ).stdout.strip().splitlines()

    # vswhere can come back empty on a machine whose installer state is out of
    # step with what is on disk, so the standard folders are looked at too.
    fallback = []
    for root in ("ProgramFiles", "ProgramFiles(x86)"):
        if os.environ.get(root):
            vs = Path(os.environ[root]) / "Microsoft Visual Studio"
            fallback += vs.glob("*/*/VC/Auxiliary/Build/vcvars64.bat")

    candidates = [Path(i) / "VC" / "Auxiliary" / "Build" / "vcvars64.bat" for i in installs]
    candidates += sorted(fallback, key=_vs_year, reverse=True)

    for vcvars in candidates:
        if vcvars.exists():
            return vcvars

    sys.exit("[plugin_probe] no Visual Studio with the x64 C++ tools found")


def _vs_year(vcvars: Path) -> int:
    # <root>\Microsoft Visual Studio\<2019|2022|18>\<edition>\VC\...: from
    # Visual Studio 2026 on the folder is the major version, 18, not a year.
    name = vcvars.parents[4].name
    number = int(name) if name.isdigit() else 0
    return number + 2008 if 0 < number < 100 else number


def build_native(out: Path) -> Path:
    out.mkdir(parents=True, exist_ok=True)
    dll = out / "guo_probe_native.dll"
    source = HERE / "native" / "probe.c"

    command = (
        f'call "{find_vcvars()}" >nul && '
        f'cl /nologo /LD /O2 /W3 "{source}" /Fe:"{dll}" user32.lib'
    )
    # One string, not a list: cmd's quoting is its own, and a list would have
    # Python escape the inner quotes in a way cmd does not undo.
    subprocess.run(f'cmd /d /s /c "{command}"', check=True, cwd=out)

    return dll


def build_managed(out: Path) -> Path:
    subprocess.run(
        ["dotnet", "build", str(HERE / "managed" / "ProbeAssistant.csproj"),
         "-c", "Release", "-o", str(out), "-nologo", "-v", "q"],
        check=True,
    )

    return out / "ProbeAssistant.dll"


def write_home(cfg: Config, home: Path, plugins: list[Path]) -> None:
    home.mkdir(parents=True, exist_ok=True)

    real = cfg.cache_dir.parent / "settings.json"
    settings = json.loads(real.read_text(encoding="utf-8-sig")) if real.exists() else {}

    for secret in ("username", "password"):
        settings[secret] = ""

    settings["ip"] = cfg.shard_host
    settings["port"] = cfg.shard_port
    settings["plugins"] = [str(p) for p in plugins]

    (home / "settings.json").write_text(json.dumps(settings, indent=2), encoding="utf-8")


def check(name: str, log: Path) -> list[str]:
    """Returns what is wrong with one plugin's log; empty when it passed."""
    if not log.exists():
        return [f"{name}: no log -- the plugin was never installed"]

    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    text = "\n".join(lines)
    faults = []

    for event in ("install", "initialize", "connected"):
        if not any(line.startswith(event) for line in lines):
            faults.append(f"{name}: never saw '{event}'")

    if not any(line.startswith("recv id=") for line in lines):
        faults.append(f"{name}: saw no packet arrive")

    if not any(line.startswith("send id=") for line in lines):
        faults.append(f"{name}: saw no packet leave")

    positions = re.findall(
        r"^position x=(-?\d+) y=(-?\d+) z=(-?\d+) get_player_position ok=(\d) "
        r"x=(-?\d+) y=(-?\d+) z=(-?\d+)",
        text, re.MULTILINE,
    )

    if not positions:
        faults.append(f"{name}: was never told the player's position")
    elif not any(p[3] == "1" and p[0:3] == p[4:7] and p[0] != "0" for p in positions):
        faults.append(f"{name}: GetPlayerPosition never agreed with the position it was told")

    return faults


def check_grow(log: Path, output: str) -> list[str]:
    """The managed probe grows one ping each way; the host must refuse both."""
    if not log.exists():
        return []

    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    faults = []

    for way in ("recv", "send"):
        grew = [i for i, line in enumerate(lines) if line.startswith(f"grow {way} ")]

        if not grew:
            faults.append(f"managed: never grew a packet on {way} (no ping seen in the world)")
            continue

        # The session must carry on after the grown packet and close cleanly.
        if not any(line.startswith("closing") for line in lines[grew[0] + 1:]):
            faults.append(f"managed: the session never closed cleanly after the grown {way} packet")

    warnings = output.count("[plugin_host] WARN a plugin grew packet")
    if warnings < 2:
        faults.append(f"host: warned about {warnings} grown packets, expected 2")

    return faults


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--no-run", action="store_true", help="build the probes and stop")
    args = parser.parse_args()

    cfg = load_config()
    work = cfg.build / "plugin_probe"
    home = work / "home"
    logs = work / "logs"

    native = build_native(work / "native")
    managed = build_managed(work / "managed")
    print(f"[plugin_probe] native : {native}")
    print(f"[plugin_probe] managed: {managed}")

    if args.no_run:
        return 0

    write_home(cfg, home, [native, managed])

    shutil.rmtree(logs, ignore_errors=True)
    logs.mkdir(parents=True)

    godot = os.environ.get("GODOT_CONSOLE") or str(cfg.godot_console_exe)
    env = dict(os.environ, GUO_PLUGIN_PROBE_LOG_DIR=str(logs))

    print(f"[plugin_probe] playing a session against {cfg.shard_host}:{cfg.shard_port}")
    session = subprocess.run(
        [godot, "--path", str(cfg.godot_project), "--",
         "--play", "--input-probe",
         # Passed rather than left to the environment, so a run from outside
         # a launcher resolves the same install and shard as one from inside.
         "--client-data", str(cfg.client_data),
         "--client-version", cfg.client_version,
         "--host", cfg.shard_host,
         "--port", str(cfg.shard_port),
         "--cache-dir", str(home / "cache"),
         "--screenshot-dir", str(cfg.build / "screenshots")],
        env=env, capture_output=True, text=True, errors="replace", timeout=900,
    )
    (work / "session.log").write_text(session.stdout + session.stderr, encoding="utf-8")

    faults = []

    if "managed plugin host bound" not in session.stdout + session.stderr:
        faults.append("client: the managed plugin host never started")

    faults += check("native", logs / "native.log")
    faults += check("managed", logs / "managed.log")
    faults += check_grow(logs / "managed.log", session.stdout + session.stderr)

    for name in ("native", "managed"):
        log = logs / f"{name}.log"
        if log.exists():
            print(f"--- {name}.log")
            print(log.read_text(encoding="utf-8", errors="replace").rstrip())

    print(f"[plugin_probe] session exit code {session.returncode}; full output in {work / 'session.log'}")

    if faults:
        for fault in faults:
            print(f"[plugin_probe] FAIL {fault}")
        return 1

    print("[plugin_probe] OK -- both plugins loaded, saw packets both ways and the player's position")
    return 0


if __name__ == "__main__":
    sys.exit(main())
