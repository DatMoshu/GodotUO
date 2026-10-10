#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-2-Clause
"""ClassicUO and GUO, live, side by side on one monitor, standing in one place.

ab_compare takes a picture of each and quits. This leaves both running, so a
difference can be walked around and looked at from every side:

    launchers\\dev\\side_by_side.bat --monitor FireTV --place britain-street
    launchers\\dev\\side_by_side.bat --at 1602 1591
    launchers\\dev\\side_by_side.bat --list-monitors

ClassicUO takes the left half of the monitor on the owner account; GUO takes
the right half on the first of UO_SHARD_GM_ACCOUNTS, because the shard drops
one of two logins on one account. The shard is pinned to daylight.

Nothing types into ClassicUO's window. Its character is put on the tile
first, by a short GUO pass that logs in as that same character, says [go and
quits; ClassicUO then logs itself in (-autologin) and comes up already there.
GUO, on its GM account, is sent to the tile with --shard-command.

    launchers\\dev\\side_by_side.bat --cuo-only --at 5401 629

starts ClassicUO alone, placed the same way, and leaves it running for a
scenario: a GUO run on the same tile that says "renderdump NAME" makes
ClassicUO write build/render_dump/NAME/cuo.json at the same moment, the
reference a render_diff step compares against.

ClassicUO is built, configured and driven by tools/ab_compare, whose code this
reuses rather than copies, so the two tools cannot drift apart.
"""

from __future__ import annotations

import argparse
import ctypes
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402


def _load_ab():
    path = Path(__file__).resolve().parents[1] / "ab_compare" / "run.py"
    spec = importlib.util.spec_from_file_location("ab_compare_run", path)
    module = importlib.util.module_from_spec(spec)
    # dataclasses looks the module up by name while it runs.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


ab = _load_ab()


# --- monitors ---------------------------------------------------------------


class _DisplayDevice(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("DeviceName", wintypes.WCHAR * 32),
        ("DeviceString", wintypes.WCHAR * 128),
        ("StateFlags", wintypes.DWORD),
        ("DeviceID", wintypes.WCHAR * 128),
        ("DeviceKey", wintypes.WCHAR * 128),
    ]


class _MonitorInfoEx(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
        ("szDevice", wintypes.WCHAR * 32),
    ]


def _friendly_names() -> dict[str, str]:
    """PnP id (e.g. AMZ0000) -> the name the monitor reports (e.g. FireTV)."""
    out = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            "Get-CimInstance -Namespace root\\wmi -ClassName WmiMonitorID | "
            "ForEach-Object { ([Text.Encoding]::ASCII.GetString($_.UserFriendlyName)).Trim([char]0) "
            "+ '|' + $_.InstanceName }",
        ],
        capture_output=True,
        text=True,
    ).stdout
    names = {}
    for line in out.splitlines():
        if "|" in line:
            name, instance = line.split("|", 1)
            parts = instance.split("\\")
            if len(parts) > 1:
                names[parts[1].upper()] = name.strip()
    return names


def monitors() -> list[dict]:
    """Every monitor: its name, PnP id, and rectangle in desktop pixels."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.SetProcessDPIAware()
    names = _friendly_names()
    found: list[dict] = []

    @ctypes.WINFUNCTYPE(
        wintypes.BOOL, wintypes.HMONITOR, wintypes.HDC, ctypes.POINTER(wintypes.RECT), wintypes.LPARAM
    )
    def each(hmon, _hdc, _rect, _lparam):
        info = _MonitorInfoEx()
        info.cbSize = ctypes.sizeof(info)
        user32.GetMonitorInfoW(hmon, ctypes.byref(info))
        dev = _DisplayDevice()
        dev.cb = ctypes.sizeof(dev)
        user32.EnumDisplayDevicesW(info.szDevice, 0, ctypes.byref(dev), 1)
        # \\?\DISPLAY#AMZ0000#... -> AMZ0000
        pnp = dev.DeviceID.split("#")[1].upper() if "#" in dev.DeviceID else ""
        r = info.rcWork
        found.append(
            {
                "device": info.szDevice,
                "pnp": pnp,
                "name": names.get(pnp, pnp or info.szDevice),
                "primary": bool(info.dwFlags & 1),
                "work": (r.left, r.top, r.right - r.left, r.bottom - r.top),
            }
        )
        return True

    user32.EnumDisplayMonitors(None, None, each, 0)
    return found


def pick_monitor(query: str | None) -> dict:
    all_monitors = monitors()
    if not query:
        return next(m for m in all_monitors if m["primary"])
    q = query.lower()
    for m in all_monitors:
        if q in m["name"].lower() or q == m["pnp"].lower() or q in m["device"].lower():
            return m
    listing = ", ".join(m["name"] for m in all_monitors)
    sys.exit(f"[sbs] no monitor matches {query!r}; there are: {listing}")


# --- the two clients -----------------------------------------------------------

# The window frame and title bar Windows adds around a client area of the size
# asked for; a half left this much short keeps both whole on the monitor.
FRAME_W, FRAME_H = 16, 40


def dump_dir(cfg) -> Path:
    """Where both clients write a render dump when "renderdump" is said."""
    return cfg.build / "render_dump"


def cuo_has_dump(cfg) -> bool:
    """Whether the ClassicUO build carries the injected render dump."""
    dll = cfg.upstream_build / "cuo.dll"
    return dll.exists() and b"RenderDumpInjected" in dll.read_bytes()


def start_cuo(cfg, character: str, x: int, y: int, w: int, h: int) -> subprocess.Popen:
    conf = ab.write_cuo_config(cfg, character, (w, h))
    settings_path = conf / "settings.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8"))
    settings["window_position"] = {"X": x, "Y": y}
    settings_path.write_text(json.dumps(settings, indent=2), encoding="utf-8")

    log = cfg.build / "side_by_side" / "cuo.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    return subprocess.Popen(
        [
            str(cfg.upstream_exe),
            "-settings", str(settings_path),
            "-username", cfg.shard_owner,
            "-password", cfg.shard_owner_password,
            "-ip", cfg.shard_host,
            "-port", str(cfg.shard_port),
            "-autologin", "true",
            "-skiploginscreen",
            "-lastcharactername", character,
            "-music", "false",
        ],
        cwd=str(cfg.upstream_build),
        stdout=log.open("w", encoding="utf-8", errors="replace"),
        stderr=subprocess.STDOUT,
        env={
            **os.environ,
            "FNA_GRAPHICS_ENABLE_HIGHDPI": "1",
            "__COMPAT_LAYER": "HighDpiAware",
            "GUO_RENDER_DUMP_DIR": str(dump_dir(cfg)),
        },
    )


def guo_home(cfg, account: str, character: str) -> Path:
    """A client home of GUO's own for this run, drawing with ClassicUO's profile.

    The GM account's saved profile is whatever its last probe left, usually a
    640x480 world view. Rather than change it, GUO gets a home here with the
    profile ClassicUO was just given, so both draw the same view with the same
    gumps. The decode cache is the real one, joined in, so start-up stays warm.
    """
    home = cfg.build / "side_by_side" / "guohome"
    home.mkdir(parents=True, exist_ok=True)

    real = Path(os.environ["LOCALAPPDATA"]) / "GUO" / "settings.json"
    if real.exists():
        shutil.copyfile(real, home / "settings.json")

    cache = home / "cache"
    if not cache.exists():
        subprocess.run(["cmd", "/c", "mklink", "/J", str(cache), str(cfg.cache_dir)],
                       check=True, capture_output=True)

    source = ab.guo_profiles_dir(cfg) / character
    dest = home / "Data" / "Profiles" / account / cfg.shard_name / account.capitalize()
    dest.mkdir(parents=True, exist_ok=True)
    for f in ("profile.json", "gumps.xml", "macros.xml", "skillsgroups.xml"):
        if (source / f).exists():
            shutil.copyfile(source / f, dest / f)
    return home


def start_guo(cfg, go: str, character: str, x: int, y: int, w: int, h: int) -> subprocess.Popen:
    if not cfg.shard_gm_accounts:
        sys.exit("[sbs] GUO needs an account of its own: set UO_SHARD_GM_ACCOUNTS")
    account = cfg.shard_gm_accounts[0]
    home = guo_home(cfg, account, character)
    log = cfg.build / "side_by_side" / "guo.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    return subprocess.Popen(
        [
            str(cfg.godot_console_exe),
            "--path", str(cfg.godot_project),
            "--",
            "--play",
            "--stay",
            # Beside ClassicUO, so the Classic look, whatever look is saved.
            "--postfx", "off",
            "--cache-dir", str(home / "cache"),
            "--account", account,
            "--password", cfg.shard_gm_password,
            "--character", account.capitalize(),
            "--window-position", f"{x},{y}",
            "--window-size", f"{w},{h}",
            "--shard-command", f"[globallight {ab.DAYLIGHT}",
            "--shard-command", go,
        ],
        stdout=log.open("w", encoding="utf-8", errors="replace"),
        stderr=subprocess.STDOUT,
        env={**ab.guo_environment(cfg), "GUO_RENDER_DUMP_DIR": str(dump_dir(cfg))},
    )


def child_pids(pid: int) -> list[int]:
    """godot-console starts the real Godot as a child, and that owns the window."""
    out = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            f"(Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}').ProcessId",
        ],
        capture_output=True,
        text=True,
    ).stdout
    return [int(s) for s in out.split() if s.isdigit()]


def wait_window(pid: int, name: str, children: bool = False, timeout: float = 90):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for p in [pid, *(child_pids(pid) if children else [])]:
            hwnd = ab.find_window(p)
            if hwnd:
                return hwnd
        time.sleep(1.0)
    raise RuntimeError(f"{name} never showed a window")


def place_window(hwnd, rect: tuple[int, int, int, int]) -> None:
    """Put the whole window, frame and all, on rect.

    Neither client can be trusted to land on another monitor by itself:
    ClassicUO pulled a negative window_position back onto the primary one.
    """
    _c, _w, user32 = ab._win32()
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    x, y, w, h = rect
    # SWP_NOZORDER | SWP_NOACTIVATE
    user32.SetWindowPos(hwnd, 0, x, y, w, h, 0x0004 | 0x0010)


def place_scenario(place) -> dict:
    """The scripted run that walks ClassicUO's character onto the place.

    It logs in with upstream's own autologin switches, never through the
    login gump: a generated shard password is longer than the gump's 16
    characters, and --play's typed login cuts it short. Autologin takes the
    account's first character, as ClassicUO does when -lastcharactername
    matches nothing; render_diff's player rows show both stood on one tile.
    """
    where = {"x": place.x, "y": place.y, "tolerance": 3, **({} if place.z is None else {"z": place.z})}
    return {
        "id": "world.place_owner",
        "title": f"Put the owner's character on {place.go[4:]} for ClassicUO",
        "surface": "client",
        "requires": {"shard": "dev", "account": "$account", "build": "debug"},
        "timeouts": {"step_s": 60, "run_s": 300},
        "steps": [
            {"id": "launch", "say": "Log the owner's character in.", "timeout_s": 240,
             "do": {"kind": "launch",
                    "args": ["--scratch-profile", "--autologin", "--account", "$account", "--password", "$password"],
                    "settings": {"autologin": True}},
             "expect": {"scene": "GameScene", "within_s": 120}},
            {"id": "settle", "say": "Wait for the world.", "do": {"kind": "wait", "seconds": 4}},
            {"id": "light", "say": "Pin the shard to daylight.",
             "do": {"kind": "chat", "text": f"[globallight {place.light}"}},
            {"id": "go", "say": "Go to the place.", "do": {"kind": "chat", "text": place.go},
             "expect": {"world.position": where, "within_s": 20}},
            {"id": "stand", "say": "Stand there a moment; the shard keeps the spot.",
             "do": {"kind": "wait", "seconds": 2}},
        ],
    }


def place_owner(cfg, place, character: str) -> None:
    """Put ClassicUO's character on the place before ClassicUO starts.

    One scripted GUO run on the owner account (tools/scenario_run, with the
    scenario above) says [globallight and [go, checks the position and
    quits. The shard keeps the character where it was left, so ClassicUO
    logs in already standing there and nothing is typed into its window.
    The password reaches the run through its environment, never a file.
    """
    print(f"[sbs] placing {cfg.shard_owner}/{character} with a scripted GUO run: {place.go}")
    out = cfg.build / "side_by_side" / "place_owner.scenario.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(place_scenario(place), indent=2), encoding="utf-8")
    env = {**os.environ,
           "GUO_SCENARIO_ACCOUNT": cfg.shard_owner,
           "GUO_SCENARIO_PASSWORD": cfg.shard_owner_password}
    runner = Path(__file__).resolve().parents[1] / "scenario_run" / "run.py"
    proc = subprocess.run([sys.executable, str(runner), str(out), "--no-register", "--no-record"],
                          env=env, cwd=str(cfg.root))
    if proc.returncode != 0:
        sys.exit(f"[sbs] the placing run failed (exit {proc.returncode}); ClassicUO was not started")


def settle_cuo(hwnd, login_wait: float) -> None:
    """Wait for ClassicUO to log in, then make it fill its window (ab.nudge: no focus, no keys)."""
    time.sleep(login_wait)
    ab.nudge(hwnd)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="side_by_side", description=__doc__.splitlines()[0])
    parser.add_argument("--monitor", default=os.environ.get("UO_COMPARE_MONITOR"),
                        help="monitor name, PnP id or \\\\.\\DISPLAYn (default: primary)")
    parser.add_argument("--place", help="a place from ab_compare, e.g. britain-street")
    parser.add_argument("--at", nargs="+", type=int, metavar="N", help="X Y [Z]")
    parser.add_argument("--character", help="ClassicUO's character (default: GUO's last played)")
    parser.add_argument("--login-wait", type=float, default=20.0)
    parser.add_argument("--dump", metavar="LABEL",
                        help="with --cuo-only only: the NAME a scenario will say (printed back); "
                             "otherwise say \"renderdump NAME\" in GUO's window")
    parser.add_argument("--cuo-only", action="store_true",
                        help="start ClassicUO alone on the place and leave it running, for a scenario's renderdump")
    parser.add_argument("--size", nargs=2, type=int, metavar=("W", "H"), default=(1280, 720),
                        help="ClassicUO's window and game window with --cuo-only (default 1280 720, a scenario client's)")
    parser.add_argument("--list-monitors", action="store_true")
    args = parser.parse_args(argv)

    if args.list_monitors:
        for m in monitors():
            x, y, w, h = m["work"]
            print(f"{m['name']:<16} {m['pnp']:<10} {w}x{h} at {x},{y}{'  (primary)' if m['primary'] else ''}")
        return 0

    if args.place:
        place = next((p for p in ab.PLACES if p.name == args.place), None)
        if place is None:
            sys.exit(f"[sbs] no place {args.place!r}; there are: {', '.join(p.name for p in ab.PLACES)}")
    elif args.at and len(args.at) in (2, 3):
        x, y, *z = args.at
        place = ab.Place(f"at-{x}-{y}", x, y, z[0] if z else None)
    else:
        sys.exit("[sbs] say where: --place NAME or --at X Y [Z]")
    if args.dump and not args.cuo_only:
        # It used to be typed into ClassicUO's window; nothing types there now.
        sys.exit("[sbs] --dump goes with --cuo-only (a scenario says the word); "
                 "side by side, say \"renderdump NAME\" in GUO's window and both dump")
    go = place.go

    cfg = load_config()
    if not cfg.upstream_exe.exists() or not cuo_has_dump(cfg):
        ab.build_cuo(cfg)

    character = args.character or ab.last_played(cfg) or "Guoprobe"
    mon = pick_monitor(args.monitor)
    mx, my, mw, mh = mon["work"]

    if args.cuo_only:
        return cuo_only(cfg, place, character, (mx, my), tuple(args.size), args.login_wait, args.dump)

    half = mw // 2
    left = (mx, my, half, mh)
    right = (mx + half, my, mw - half, mh)
    w, h = half - FRAME_W, mh - FRAME_H
    print(f"[sbs] {mon['name']}: {mw}x{mh} at {mx},{my}; each client about {w}x{h}")
    print(f"[sbs] both to {go[1:]}")

    place_owner(cfg, place, character)
    cuo = start_cuo(cfg, character, mx, my, w, h)
    print(f"[sbs] ClassicUO (pid {cuo.pid}) on the left, as {cfg.shard_owner}/{character}")
    guo = start_guo(cfg, go, character, mx + half, my, w, h)
    print(f"[sbs] GUO (pid {guo.pid}) on the right, as {cfg.shard_gm_accounts[0]}")

    cuo_hwnd = wait_window(cuo.pid, "ClassicUO")
    place_window(cuo_hwnd, left)
    guo_hwnd = wait_window(guo.pid, "GUO", children=True)
    place_window(guo_hwnd, right)

    settle_cuo(cuo_hwnd, args.login_wait)
    # Both move their own windows as they enter the world (ClassicUO applies
    # the profile's window then), so the places are set again after it.
    time.sleep(args.login_wait / 2)
    place_window(cuo_hwnd, left)
    ab.nudge(cuo_hwnd)
    place_window(guo_hwnd, right)

    print("[sbs] Both in place. Say \"renderdump NAME\" in GUO's window to dump both;")
    print(r"[sbs] compare with: launchers\dev\render_diff.bat NAME")
    print("[sbs] Close either window when done.")
    return 0


def cuo_only(cfg, place, character: str, origin: tuple[int, int], size: tuple[int, int],
             login_wait: float, dump: str | None) -> int:
    """ClassicUO alone, standing on the place and listening for "renderdump"; left running.

    The game window is set to the client window's size, which is how GUO
    frames its own, so a scenario's GUO at the same size draws the same part
    of the world. Its pid goes to build/side_by_side/cuo.pid for whoever
    stops it.
    """
    w, h = size
    place_owner(cfg, place, character)
    cuo = start_cuo(cfg, character, origin[0], origin[1], w, h)
    hwnd = wait_window(cuo.pid, "ClassicUO")
    settle_cuo(hwnd, login_wait)
    (cfg.build / "side_by_side" / "cuo.pid").write_text(str(cuo.pid), encoding="ascii")
    name = dump or "NAME"
    print(f"[sbs] ClassicUO (pid {cuo.pid}) is on {place.go[4:]} as {cfg.shard_owner}/{character}, {w}x{h}")
    print(f"[sbs] a GUO run there saying \"renderdump {name}\" makes it write {dump_dir(cfg) / name / 'cuo.json'}")
    print("[sbs] Close its window, or end that pid, when done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
