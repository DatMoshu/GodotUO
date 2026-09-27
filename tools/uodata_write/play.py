#!/usr/bin/env python3
"""Walk in game with authored data (ADR-0022, H3): the staged set on the private shard.

    python tools/uodata_write/play.py --stage DIR [--item 0xFFF0] [--clip OUT.mp4] [--out DIR]

1. The private ModernUO (tools/editor_shard, 127.0.0.1:2594) starts with the
   bridge and the stage first in its data directories, so the server reads the
   staged tiledata (the new item's layer and animation).
2. A client logs in with settings.json files_override = the stage's
   files_override.txt, so it reads the staged art, gumps, animation and
   tiledata. Its window never takes the focus.
3. The bridge equips the item on the character (op "equip").
4. The client's world dump confirms the item is worn; a frame is taken (the
   paperdoll, open since login, shows it).
5. With --clip: the character walks the four screen diagonals while the client
   records, and ffmpeg writes a captioned MP4.

Never the shared shard. Exit 0 when the item is worn and (with --clip) the walk was recorded.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402

PORT, BRIDGE = 2594, 2595
CHARACTER = "asdfdsaf"  # the probe account's character in the private shard's copied saves


def sh(*args: str) -> int:
    return subprocess.run([sys.executable, *args]).returncode


def wait_for(pred, timeout: float) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


def bridge_equip(item: int) -> dict:
    with socket.create_connection(("127.0.0.1", BRIDGE), timeout=10) as s:
        f = s.makefile("rw", encoding="utf-8", newline="\n")
        f.write(json.dumps({"op": "hello", "editor": "uodata-play"}) + "\n")
        f.write(json.dumps({"op": "equip", "as": CHARACTER, "item_id": item}) + "\n")
        f.flush()
        s.settimeout(30)
        for line in f:
            msg = json.loads(line)
            if msg.get("op") in ("equip", "error"):
                return msg
    return {"ok": False, "error": "no answer"}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--stage", type=Path, required=True)
    ap.add_argument("--item", default=None, help="item id (default: the stage's dreadcrest.json)")
    ap.add_argument("--clip", type=Path)
    ap.add_argument("--caption", default="GUO: the Dreadcrest shield, authored into a staged data set, worn in game")
    ap.add_argument("--out", type=Path)
    args = ap.parse_args()
    cfg = load_config()
    stage = args.stage.resolve()
    item = int(args.item, 0) if args.item else json.loads((stage / "dreadcrest.json").read_text())["item"]
    out = (args.out or cfg.build / "uodata_play").resolve()
    if out.exists():
        shutil.rmtree(out)
    watch = out / "watch"
    watch.mkdir(parents=True)
    tools = cfg.tools

    sh(str(tools / "editor_shard" / "run.py"), "stop")
    if sh(str(tools / "editor_shard" / "run.py"), "bridge") != 0:
        return 2
    if sh(str(tools / "editor_shard" / "run.py"), "start", "--data-first", str(stage)) != 0:
        return 2

    home = out / "client_home"
    (home / "cache").mkdir(parents=True)
    (home / "profiles").mkdir()
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles"),
                                                    "files_override": str(stage / "files_override.txt")}), encoding="utf-8")
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
    cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--window-size", "1024,768",
           "--screenshot-dir", str(out), "--screenshot-name", "end", "--objects-watch", str(watch),
           "--shard-command", "[self set map felucca", "--shard-command", "[go 1164 1668", "--shard-command", "[where"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
    client = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                              stderr=subprocess.STDOUT, env=env, **no_activate())
    report = {"item": item, "stage": str(stage)}
    try:
        if not wait_for(lambda: (watch / "watching").exists(), 300):
            print("[play] the client never reached its watch")
            return 1
        report["equip"] = bridge_equip(item)
        print(f"[play] equip: {report['equip']}")
        time.sleep(3)

        def look(name: str) -> dict:
            (watch / f"{name}.request").write_text("", encoding="utf-8")
            (watch / f"{name}.shot").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / f"{name}.json").exists() and (watch / f"{name}.png").exists(), 60)
            time.sleep(0.5)
            return json.loads((watch / f"{name}.json").read_text(encoding="utf-8"))

        seen = look("equipped")
        report["worn_seen_by_client"] = report["equip"].get("ok") and any(
            int(o["graphic"], 16) == item for o in seen.get("worn", []) + seen.get("items", []))
        if args.clip:
            (watch / "walk.rec").write_text("18 10", encoding="utf-8")
            time.sleep(1.5)
            (watch / "walk.walk").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / "walk.walked").exists() and (watch / "walk.recorded").exists(), 120)
            report["walked"] = (watch / "walk.walked").read_text() if (watch / "walk.walked").exists() else None
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        time.sleep(2)
        if client.poll() is None:
            client.kill()
        sh(str(tools / "editor_shard" / "run.py"), "stop")

    if args.clip:
        args.clip = args.clip.resolve()
    if args.clip and (watch / "walk").is_dir():
        font = "C\\\\:/Windows/Fonts/arial.ttf"
        # The caption goes through a file beside the frames: commas and colons in
        # it would otherwise be read as filtergraph syntax.
        (out / "caption.txt").write_text(args.caption, encoding="utf-8")
        vf = (f"drawtext=fontfile='{font}':textfile=caption.txt:x=16:y=h-36:fontsize=20:fontcolor=white:"
              "box=1:boxcolor=black@0.55:boxborderw=6")
        args.clip.parent.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", "10", "-i", str(watch / "walk" / "%04d.png"),
                            "-vf", vf, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "28", "-movflags", "+faststart",
                            str(args.clip)], capture_output=True, text=True, cwd=out)
        report["clip"] = str(args.clip) if r.returncode == 0 else f"ffmpeg failed: {r.stderr[-300:]}"
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "stage"}, indent=1))
    return 0 if report.get("equip", {}).get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
