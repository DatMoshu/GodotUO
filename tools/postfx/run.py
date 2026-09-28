#!/usr/bin/env python3
r"""The post-processing proof run (ADR-0023): every look, photographed and timed.

    python tools\postfx\run.py sheet [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR]
    python tools\postfx\run.py tour  [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR] [--fps 30]
    python tools\postfx\run.py luts          (the built-in LUTs; see luts.py)

sheet:
1. A private ModernUO copy of this checkout (tools/editor_shard, in this
   checkout's build\shard_private) on 127.0.0.1:PORT, with no bridge. Never
   the shared shard (2593) and not the editor's usual port (2594).
2. A client logs in as the probe account with a throwaway home, its window
   never taking the focus, is sent to one spot, and runs --postfx-sheet:
   Classic hands back the world texture itself; an identity pass through the
   whole stack is compared with the world target pixel for pixel in one
   frame; then every shader alone and every preset is photographed, with the
   post-processing viewport's GPU time averaged over 120 frames.
3. A contact sheet (sheet.png) of the looks, labelled with their cost, is
   made from the frames, and the shard is stopped.

Output: build\postfx_sheet\<time>\ (report.json, one PNG per look, sheet.png,
client.log). Exit 0 when the probe passed.

tour: the same shard and login, then the client runs --postfx-sheet DIR
--postfx-tour under Godot's own movie writer (--write-movie tour.avi
--fixed-fps FPS --max-fps FPS, so it records at real time and never needs the
focus or a screen grab): a walk while the look switches, then the menu with
Compare with Classic and two sliders moving (PostFxProbe.Tour). tour.json
gives each beat's frame in tour.avi. The AVI carries the game's audio; drop it
(ffmpeg -an) for anything posted. The menu's own GPU readout runs high while
the movie writer is on, so take GPU numbers from a sheet run.
Output: build\postfx_tour\<time>\ (tour.avi, tour.json, client.log).
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402


def say(msg: str) -> None:
    print(f"[postfx] {msg}", flush=True)


def shard(tools: Path, *args: str) -> int:
    return subprocess.run([sys.executable, str(tools / "editor_shard" / "run.py"), *args]).returncode


def sheet(args, tour: bool = False) -> int:
    cfg = load_config()
    out = (args.out or cfg.build / ("postfx_tour" if tour else "postfx_sheet") / time.strftime("%Y%m%d-%H%M%S")).resolve()
    out.mkdir(parents=True, exist_ok=True)
    say(f"output: {out}")
    tools = cfg.tools

    state = cfg.build / "shard_private" / "state.json"
    port_ok = False
    if state.exists():
        try:
            port_ok = json.loads(state.read_text(encoding="utf-8")).get("port") == args.port
        except (OSError, ValueError):
            port_ok = False
    if not port_ok and shard(tools, "setup", "--port", str(args.port)) != 0:
        return 2
    shard(tools, "stop")
    if shard(tools, "start", "--no-bridge") != 0:
        return 2

    home = out / "client_home"
    (home / "cache").mkdir(parents=True, exist_ok=True)
    (home / "profiles").mkdir(exist_ok=True)
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
    x, y = args.at.split(",")
    movie = ["--write-movie", str(out / "tour.avi"), "--fixed-fps", str(args.fps), "--max-fps", str(args.fps)] if tour else []
    cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), *movie, "--", "--play",
           "--window-size", args.size, "--postfx-sheet", str(out), *(["--postfx-tour"] if tour else []),
           "--shard-command", "[self set map felucca", "--shard-command", f"[go {x} {y}"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(args.port)}
    try:
        with (out / "client.log").open("w", encoding="utf-8", errors="replace") as log:
            rc = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, timeout=1500, **no_activate()).returncode
    except subprocess.TimeoutExpired:
        rc = -1
    finally:
        shard(tools, "stop")

    for line in (out / "client.log").read_text(encoding="utf-8", errors="replace").splitlines():
        if "postfx probe" in line or "SHADER ERROR" in line or "shader error" in line.lower():
            print("  " + line.strip()[:200])
    report = out / "report.json"
    if not tour and report.exists():
        make_sheet(out, json.loads(report.read_text(encoding="utf-8")))
    say(f"client exit {rc}; {out}")
    return 0 if rc == 0 else 1


def device(args) -> int:
    """The same proof on an Android device (the Thor): an APK with the probe baked
    in, the private shard reached through adb reverse, the report and frames
    pulled back with run-as, then the app stopped and the device put to sleep."""
    cfg = load_config()
    sys.path.insert(0, str(cfg.tools / "android"))
    import run as android  # tools/android/run.py

    p = android.Paths(cfg)
    out = (args.out or cfg.build / "postfx_device" / time.strftime("%Y%m%d-%H%M%S")).resolve()
    out.mkdir(parents=True, exist_ok=True)
    say(f"output: {out}")
    tools = cfg.tools
    state = cfg.build / "shard_private" / "state.json"
    port_ok = state.exists() and json.loads(state.read_text(encoding="utf-8")).get("port") == args.port
    if not port_ok and shard(tools, "setup", "--port", str(args.port)) != 0:
        return 2
    shard(tools, "stop")
    if shard(tools, "start", "--no-bridge") != 0:
        return 2
    rc = 1
    try:
        adb = android.adb_cmd(p)
        if subprocess.run(adb + ["reverse", f"tcp:{args.port}", f"tcp:{args.port}"]).returncode != 0:
            say("adb reverse failed; is the device connected?")
            return 2
        x, y = args.at.split(",")
        extra = (f'--host 127.0.0.1 --port {args.port} --account guoprobe --autologin --postfx-sheet user://postfx_sheet '
                 f'--shard-command "[self set map felucca" --shard-command "[go {x} {y}"')
        apk = p.out_dir / "GUO-postfx.apk"
        if not args.no_export and android.export(p, extra, apk) != 0:
            return 1
        if android.install(p, apk) != 0:
            return 1
        subprocess.run(adb + ["shell", "run-as", cfg.android_package, "rm", "-rf", "files/postfx_sheet"])
        subprocess.run(adb + ["logcat", "-c"])
        if android.start_app(p) != 0:
            return 1
        log = out / "logcat.txt"
        done = False
        with log.open("w", encoding="utf-8", errors="replace") as f:
            proc = subprocess.Popen(adb + ["logcat", "-v", "time"] + android.LOGCAT_FILTER, stdout=subprocess.PIPE,
                                    text=True, encoding="utf-8", errors="replace")
            t0 = time.time()
            try:
                for line in proc.stdout:
                    f.write(line)
                    if "postfx probe" in line:
                        print("  " + line.strip()[-160:])
                    if "postfx probe: ok" in line or "postfx probe: FAIL" in line or "postfx probe: never got into" in line:
                        done = "postfx probe: ok" in line
                        break
                    if time.time() - t0 > args.timeout:
                        say("timeout")
                        break
            finally:
                proc.kill()
        # Pull the report and frames out of the app's own files.
        listing = subprocess.run(adb + ["shell", "run-as", cfg.android_package, "ls", "files/postfx_sheet"],
                                 capture_output=True, text=True).stdout.split()
        for name in listing:
            with (out / name).open("wb") as f:
                subprocess.run(adb + ["exec-out", "run-as", cfg.android_package, "cat", f"files/postfx_sheet/{name}"], stdout=f)
        say(f"pulled {len(listing)} files")
        if (out / "report.json").exists():
            make_sheet(out, json.loads((out / "report.json").read_text(encoding="utf-8")))
        rc = 0 if done else 1
    finally:
        # Always stop the shard; the device steps may fail if no single device is picked.
        try:
            android.stop_app(p)
            # Asleep again, as the device's other users expect.
            subprocess.run(android.adb_cmd(p) + ["shell", "input", "keyevent", "KEYCODE_SLEEP"])
            subprocess.run(android.adb_cmd(p) + ["reverse", "--remove", f"tcp:{args.port}"])
        except SystemExit:
            pass
        shard(tools, "stop")
    say(f"device run {'ok' if rc == 0 else 'FAILED'}; {out}")
    return rc


def make_sheet(out: Path, report: dict) -> None:
    """A grid of every look, each labelled with its name and GPU cost."""
    from PIL import Image, ImageDraw

    tiles = [("Classic", "00_classic.png", None)]
    tiles += [(name, v["png"], v["gpu_ms"]) for name, v in report.get("presets", {}).items()]
    tiles += [("A/B split (Noir)", "zz_split_noir.png", None)]
    tiles = [t for t in tiles if (out / t[1]).exists()]
    if not tiles:
        return
    first = Image.open(out / tiles[0][1])
    w, h = first.size
    scale = 0.36
    tw, th = int(w * scale), int(h * scale)
    cols = 4
    rows = (len(tiles) + cols - 1) // cols
    label = 22
    sheet = Image.new("RGB", (cols * tw, rows * (th + label)), (20, 25, 23))
    draw = ImageDraw.Draw(sheet)
    for i, (name, png, ms) in enumerate(tiles):
        im = Image.open(out / png).convert("RGB").resize((tw, th), Image.Resampling.NEAREST)
        cx, cy = (i % cols) * tw, (i // cols) * (th + label)
        sheet.paste(im, (cx, cy + label))
        text = name + (f"  {ms:.3f} ms" if ms is not None else "")
        draw.text((cx + 6, cy + 5), text, fill=(223, 187, 119))
    sheet.save(out / "sheet.png")
    say(f"contact sheet: {out / 'sheet.png'} ({len(tiles)} looks)")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sheet")
    s.add_argument("--port", type=int, default=2596)
    s.add_argument("--at", default="1475,1645", help="x,y on Felucca to stand at (default: Britain)")
    s.add_argument("--size", default="1280,800")
    s.add_argument("--out", type=Path)
    t = sub.add_parser("tour", help="a live walk and menu tour, recorded with Godot's --write-movie")
    t.add_argument("--port", type=int, default=2596)
    t.add_argument("--at", default="1475,1645", help="x,y on Felucca to stand at (default: Britain)")
    t.add_argument("--size", default="1280,800")
    t.add_argument("--out", type=Path)
    t.add_argument("--fps", type=int, default=30)
    d = sub.add_parser("device", help="the same proof on the attached Android device (adb)")
    d.add_argument("--port", type=int, default=2596)
    d.add_argument("--at", default="1475,1645")
    d.add_argument("--out", type=Path)
    d.add_argument("--timeout", type=int, default=900)
    d.add_argument("--no-export", action="store_true", help="reuse build/android/GUO-postfx.apk")
    sub.add_parser("luts")
    args = ap.parse_args()
    if args.cmd == "luts":
        import luts  # noqa: F401  (tools/postfx/luts.py)

        return luts.main()
    if args.cmd == "device":
        return device(args)
    return sheet(args, tour=args.cmd == "tour")


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    sys.exit(main())
