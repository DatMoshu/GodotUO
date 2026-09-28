r"""Visual A/B (B7): ClassicUO and GUO, with and without the land array, in the same spots.

    python tools/visual_ab/run.py [--port 2598] [--size 1600,900] [--build] [--scene NAME ...]
    python tools/visual_ab/run.py --sheets        (redraw the sheets from shots already taken)

Each client runs once per scene with GUO_SHOT_DUMP set (godot/GUO/src/Bootstrap/
ShotDump.cs, compiled into both, as PerfDump is): it jumps there with [go, waits
for the fade-in, then does the scene's steps -- shard commands, a door, a
profile setting -- and saves its own screenshot after each. No keystrokes and
no focus: ClassicUO logs in with -autologin, GUO with --play, both as the shard
owner and the same character with the same profile (tools/ab_compare).

GUO runs once per variant: plain, --merged-land=array, and that with
--merged-cover. Output, per scene and shot, in build/visual_ab/<scene>/:
VARIANT_SHOT.png from each client, SHOT_sheet.png (the four, labelled), and
SHOT_diff_<variant>.png: where that GUO variant differs from plain GUO (red),
since the flags must change nothing on screen. Mobiles wander and blood is a
random graphic, so ClassicUO against GUO is for the eye; the GUO variants
against each other are for the diff.
"""
from __future__ import annotations

import argparse
import dataclasses
import importlib.util
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from guo.build import build_client  # noqa: E402
from guo.config import Config, load_config  # noqa: E402

_spec = importlib.util.spec_from_file_location("ab_compare", HERE.parent / "ab_compare" / "run.py")
ab = importlib.util.module_from_spec(_spec)
sys.modules["ab_compare"] = ab
_spec.loader.exec_module(ab)

# (name, x, y, z or None, steps). Places from the owner's B7 conditions:
# statics below the ground (a scan of statics0 for statics 15+ below their land),
# ground items behind a roof (the Britain healers, roof x 1468-1480, y 1608-1616,
# the street behind it at z 20), and a door by a roof, from outside and inside.
SCENES = [
    ("basement-east", 1612, 1592, None, "shot:view"),
    ("basement-bank", 1444, 1672, None, "shot:view"),
    ("healers-roof", 1484, 1622, None,
     "shot:before|say:[TileXYZ 1468 1606 13 1 20 Blood|wait:1000|shot:blood"),
    ("healers-door-outside", 1482, 1612, None, "shot:closed|door|shot:open|door|shot:closed-again"),
    ("healers-door-inside", 1474, 1612, 20, "shot:closed|door|shot:open|door|shot:closed-again"),
    ("britain-street", 1602, 1591, None, "shot:view"),
    # Second pass, zoomed out (--zoom 1.5), gumps hidden. The owner's cases: the
    # player BEHIND a building (north or west of it, the roof on), with blood and
    # small items between the player and the back wall, where the roof must hide
    # them; an interior door (a door between two rooms, found by
    # tools/visual_ab's door probe) from inside with the roof hidden and from
    # outside with it on; mid-step shots for "looks different when moving"; and
    # a sweep from [go britain (1495,1629,10), the owner's "teleport spot".
    # Items are statics (they do not decay) added again by each client: the same
    # graphic twice on a tile draws the same.
    ("behind-healers-north", 1474, 1604, None,
     "hide|say:[TileXYZ 1468 1606 13 1 {z} Static 0x122A|say:[TileXYZ 1471 1605 2 1 {z} Static 0x0EED"
     "|say:[TileXYZ 1477 1605 2 1 {z} Static 0x0E21|wait:800|shot:items"),
    ("behind-healers-west", 1463, 1612, None,
     "hide|say:[TileXYZ 1466 1608 1 9 {z} Static 0x122A|say:[TileXYZ 1465 1610 1 2 {z} Static 0x0EED"
     "|wait:800|shot:items"),
    ("behind-neighbour-north", 1454, 1604, None,
     "hide|say:[TileXYZ 1448 1606 13 1 {z} Static 0x122A|say:[TileXYZ 1452 1605 2 1 {z} Static 0x0E21"
     "|wait:800|shot:items"),
    ("door-a-inside", 1547, 1652, 26, "hide|shot:closed|door:1547,1655|shot:open|door:1547,1655|shot:closed-again"),
    ("door-a-outside-north", 1547, 1644, None,
     "hide|shot:closed|door:1547,1655|shot:open|door:1547,1655|shot:closed-again"),
    ("door-a-outside-south", 1547, 1666, None,
     "hide|shot:closed|door:1547,1655|shot:open|door:1547,1655|shot:closed-again"),
    ("door-b-inside", 1602, 1651, 10, "hide|shot:closed|door:1599,1651|shot:open|door:1599,1651|shot:closed-again"),
    ("door-b-outside-east", 1610, 1651, None,
     "hide|shot:closed|door:1599,1651|shot:open|door:1599,1651|shot:closed-again"),
    ("go-britain", 1495, 1629, 10, "hide|shot:view"),
    ("sweep-west", 1462, 1628, None, "hide|shot:view"),
    ("moongate", 1336, 1997, None, "hide|shot:view"),
    ("walk-mid-step", 1495, 1629, 10,
     "hide|shot:still|walk:West|shot:mid1|walk:West|shot:mid2|walk:West:run|shot:mid3|walk:Up:run|shot:mid4"),
    # River banks, shot close (--zoom 0.6): stretched grass rising from the
    # water's z over the water statics on it (ADR-0004's covering land). The
    # player stands in the water; 1546,1572 and 1403,1665 came from a scan of
    # statics0 for water with land two tiles on 10+ z above it.
    ("river-healers", 1522, 1646, None, "hide|shot:view"),
    ("river-north", 1546, 1572, None, "hide|shot:view"),
    ("river-west", 1403, 1665, None, "hide|shot:view"),
]

VARIANTS = {
    "guo": [],
    "guo-array": ["--merged-land=array"],
    "guo-cover": ["--merged-land=array", "--merged-cover"],
}


def spec(out: Path, scene: tuple, zoom: float) -> str:
    name, x, y, z, steps = scene
    # Daylight pinned first, as ab_compare does: GUO also asks with --shard-command,
    # ClassicUO has only this. The mouse-over highlight off (restored at the end):
    # GUO highlights whatever tile is under the desktop's cursor, ClassicUO's
    # window has no mouse over it, and the highlight (hue 0x14, purple) looked
    # like a drawing bug in the first two passes.
    return (f"{out};{name};{x};{y};{'' if z is None else z};{zoom};"
            f"say:[globallight {ab.DAYLIGHT}|set:HighlightGameObjects=False|{steps}")


def wait_for(path: Path, proc: subprocess.Popen, timeout: float) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.exists():
            return True
        if proc.poll() is not None:
            return False
        time.sleep(1.0)
    return False


def end(proc: subprocess.Popen) -> None:
    proc.terminate()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()


class Tucked(threading.Thread):
    """Keeps a client's windows just right of every monitor while it runs.

    Both clients photograph their own back buffer, which draws the same off the
    desktop as on it, so a run need not be seen: shown, it lands on the owner's
    screen, and on a scaled display ClassicUO's is its size times the scale
    (FNA_GRAPHICS_ENABLE_HIGHDPI), too big to fit. Neither client can be told to
    start off the screen -- ClassicUO clamps its position into a display
    (GameController.SetWindowPositionBySettings) and moves again on login -- so
    its windows, and those of the process it starts (godot-console runs the
    editor binary), are moved back out whenever they come in. Without taking
    focus: the Godot run is --no-focus, and SDL is asked not to activate.
    """

    def __init__(self, proc: subprocess.Popen) -> None:
        super().__init__(daemon=True)
        self.pid = proc.pid
        self.done = threading.Event()

    def run(self) -> None:
        import ctypes
        from ctypes import wintypes

        try:
            user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
        except AttributeError:
            return
        ab.display_scale()  # per-monitor DPI aware first, or the metrics come back in scaled units
        # SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN: the desktop across all monitors.
        right = user32.GetSystemMetrics(76) + user32.GetSystemMetrics(78)
        top = user32.GetSystemMetrics(77)

        class ENTRY(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                        ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wintypes.DWORD),
                        ("cntThreads", wintypes.DWORD), ("th32ParentProcessID", wintypes.DWORD),
                        ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD), ("szExeFile", ctypes.c_char * 260)]

        kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE

        def family() -> set[int]:
            snap = kernel32.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
            parents = {}
            e = ENTRY()
            e.dwSize = ctypes.sizeof(ENTRY)
            ok = kernel32.Process32First(snap, ctypes.byref(e))
            while ok:
                parents[e.th32ProcessID] = e.th32ParentProcessID
                ok = kernel32.Process32Next(snap, ctypes.byref(e))
            kernel32.CloseHandle(snap)
            pids = {self.pid}
            grew = True
            while grew:
                more = {p for p, parent in parents.items() if parent in pids} - pids
                pids |= more
                grew = bool(more)
            return pids

        proc_type = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
        while not self.done.wait(0.05):
            pids = family()

            def tuck(hwnd, _):
                pid = wintypes.DWORD()
                user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                if pid.value in pids and user32.IsWindowVisible(hwnd):
                    r = wintypes.RECT()
                    user32.GetWindowRect(hwnd, ctypes.byref(r))
                    if r.left < right:
                        # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
                        user32.SetWindowPos(hwnd, None, right + 64, top, 0, 0, 0x0001 | 0x0004 | 0x0010)
                return True

            user32.EnumWindows(proc_type(tuck), 0)


def run_guo(cfg: Config, out: Path, scene: tuple, variant: str, size: str, s: str, timeout: float) -> bool:
    folder = out / scene[0]
    folder.mkdir(parents=True, exist_ok=True)
    env = {**ab.guo_environment(cfg), "GUO_SHOT_DUMP": s, "GUO_SHOT_VARIANT": variant}
    proc = subprocess.Popen(
        [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--silent",
         "--window-size", size, "--no-focus", "--account", cfg.shard_owner, "--password", cfg.shard_owner_password,
         "--postfx", "off",  # the Classic look, whatever look is saved
         "--shard-command", f"[globallight {ab.DAYLIGHT}", "--stay", *VARIANTS[variant]],
        stdout=(folder / f"{variant}.log").open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT, env=env,
    )
    tucked = Tucked(proc)
    tucked.start()
    try:
        return wait_for(folder / f"{variant}.done", proc, timeout)
    finally:
        tucked.done.set()
        end(proc)


def run_cuo(cfg: Config, out: Path, scene: tuple, size: tuple[int, int], s: str, timeout: float) -> bool:
    folder = out / scene[0]
    folder.mkdir(parents=True, exist_ok=True)
    character = ab.last_played(cfg) or ""
    conf = ab.write_cuo_config(cfg, character, size)
    proc = subprocess.Popen(
        [str(cfg.upstream_exe), "-settings", str(conf / "settings.json"),
         "-username", cfg.shard_owner, "-password", cfg.shard_owner_password,
         "-ip", cfg.shard_host, "-port", str(cfg.shard_port), "-autologin", "true", "-skiploginscreen",
         "-lastcharactername", character, "-music", "false"],
        cwd=str(cfg.upstream_build),
        stdout=(folder / "cuo.log").open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT,
        env={**os.environ, "FNA_GRAPHICS_ENABLE_HIGHDPI": "1", "__COMPAT_LAYER": "HighDpiAware",
             "GUO_SHOT_DUMP": s, "SDL_WINDOW_ACTIVATE_WHEN_SHOWN": "0"},
    )
    tucked = Tucked(proc)
    tucked.start()
    try:
        return wait_for(folder / "cuo.done", proc, timeout)
    finally:
        tucked.done.set()
        end(proc)


def sheets(out: Path) -> list[str]:
    """Per scene and shot: the four side by side, and each GUO variant's difference from plain GUO."""
    from PIL import Image, ImageChops, ImageDraw

    lines = []
    for scene in SCENES:
        folder = out / scene[0]
        shots = [step.split(":", 1)[1] for step in scene[4].split("|") if step.startswith("shot:")]
        for shot in shots:
            names = ["cuo", *VARIANTS]
            pics = {n: Image.open(folder / f"{n}_{shot}.png").convert("RGB")
                    for n in names if (folder / f"{n}_{shot}.png").exists()}
            if not pics:
                continue
            w, h = max(p.width for p in pics.values()), max(p.height for p in pics.values())
            sheet = Image.new("RGB", (w * 2, (h + 24) * 2), (32, 32, 32))
            for i, n in enumerate(names):
                x, y = (i % 2) * w, (i // 2) * (h + 24)
                ImageDraw.Draw(sheet).text((x + 8, y + 4), n, fill=(255, 255, 0))
                if n in pics:
                    sheet.paste(pics[n], (x, y + 24))
            sheet.save(folder / f"{shot}_sheet.png")
            row = [f"{scene[0]}/{shot}"]
            for n in ("guo-array", "guo-cover"):
                if "guo" not in pics or n not in pics or pics[n].size != pics["guo"].size:
                    row.append("-")
                    continue
                diff = ImageChops.difference(pics["guo"], pics[n]).convert("L").point(lambda v: 255 if v > 0 else 0)
                count = sum(1 for v in diff.tobytes() if v)
                marked = pics["guo"].copy()
                marked.paste((255, 0, 0), mask=diff)
                marked.save(folder / f"{shot}_diff_{n}.png")
                row.append(f"{count} px")
            lines.append("| " + " | ".join(row) + " |")
    return ["| scene/shot | guo-array vs guo | guo-cover vs guo |", "|---|---:|---:|", *lines]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, help="shard port (default UO_SHARD_PORT)")
    ap.add_argument("--size", default="1600,900", help="window size of both clients")
    ap.add_argument("--zoom", type=float, default=0, help="camera zoom (0 keeps the profile's)")
    ap.add_argument("--timeout", type=float, default=180, help="per client and scene")
    ap.add_argument("--build", action="store_true", help="build ClassicUO first (tools/ab_compare)")
    ap.add_argument("--scene", action="append", choices=[s[0] for s in SCENES], help="these scenes only")
    ap.add_argument("--only", choices=["cuo", *VARIANTS], action="append", help="these clients/variants only")
    ap.add_argument("--sheets", action="store_true", help="only redraw the sheets and diffs")
    ap.add_argument("--as-built", action="store_true", help="run GUO as built (no optimised rebuild)")
    args = ap.parse_args()

    cfg = load_config()
    if args.port:
        cfg = dataclasses.replace(cfg, shard_port=args.port)
    out = cfg.build / "visual_ab"
    if not args.sheets:
        size = tuple(int(v) for v in args.size.split(","))
        if args.build or not cfg.upstream_exe.exists():
            ab.build_cuo(cfg)
        if not args.as_built:
            build_client(cfg)
        failed = 0
        for scene in [s for s in SCENES if not args.scene or s[0] in args.scene]:
            s = spec(out, scene, args.zoom)
            for client in ["cuo", *VARIANTS]:
                if args.only and client not in args.only:
                    continue
                (out / scene[0] / f"{client}.done").unlink(missing_ok=True)
                ok = (run_cuo(cfg, out, scene, size, s, args.timeout) if client == "cuo"
                      else run_guo(cfg, out, scene, client, args.size, s, args.timeout))
                print(f"[visual_ab] {scene[0]} {client}: {'ok' if ok else 'NO RESULT (see its log)'}", flush=True)
                failed += not ok
        if failed:
            print(f"[visual_ab] {failed} run(s) without a result")
    print("\n".join(sheets(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
