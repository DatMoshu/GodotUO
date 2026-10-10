#!/usr/bin/env python3
"""The Astral Wayfarer showcase: wear the outfit, then take it off piece by piece (ADR-0022).

    python tools/uodata_write/outfit_video.py --stage build/uodata/astral [--out DIR] [--frames 70]

1. The private ModernUO shard (127.0.0.1:2594, never the shared one) starts with
   the stage first in its data directories and the editor bridge.
2. A GUO client, windowed and never focused, logs in reading the stage
   (settings.json files_override) and goes to an open field.
3. The bridge puts on the whole outfit ("equip-many", the robe last so it is
   over everything). The character walks all eight directions while the
   client records.
4. The robe comes off, then the lightsaber, gloves, shoes, shirt, pants and
   hair, one at a time ("unequip"), with the eight-direction walk recorded
   after each.
5. ffmpeg captions each segment with what was just taken off and joins them:
   a master (crf 18) and a Discord cut under 10 MB.

Ids come from the stage's astral_ids.json (uodata_write outfit). Exit 0 when
every step was acknowledged and every segment recorded.
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

from guo import load_config, shard_secrets  # noqa: E402
from guo.process import no_activate  # noqa: E402

PORT, BRIDGE = 2594, 2595
CHARACTER = "asdfdsaf"  # the probe account's character in the private shard's copied saves
START = "[go 1164 1668"  # an open field in Felucca; every segment starts here
DISCORD_CROP = "800:600:90:80"  # the paperdoll and the character, at native size
WEAR_ORDER = ("sword", "shirt", "pants", "shoes", "gloves", "hair", "robe")
SEGMENTS = [
    (None, "Astral Wayfarer: the full outfit, robe over everything"),
    ("robe", "Robe off: the astral shirt and pants show"),
    ("sword", "Lightsaber off"),
    ("gloves", "Gloves off"),
    ("shoes", "Shoes off"),
    ("shirt", "Shirt off"),
    ("pants", "Pants off"),
    ("hair", "Hair off"),
]


def sh(*args: str) -> int:
    return subprocess.run([sys.executable, *args]).returncode


def wait_for(pred, timeout: float) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


def admin_token() -> str:
    """The bridge's admin token (ADR-0035): the "command" op needs it since AD3. Never printed."""
    cfg = load_config()
    if cfg.bridge_admin_token:
        return cfg.bridge_admin_token
    path, _ = shard_secrets.ensure(cfg.workspace_dir)
    return shard_secrets.read(path).get(shard_secrets.ADMIN_TOKEN_KEY, "")


def bridge(op: dict) -> dict:
    with socket.create_connection(("127.0.0.1", BRIDGE), timeout=10) as s:
        f = s.makefile("rw", encoding="utf-8", newline="\n")
        f.write(json.dumps({"op": "hello", "editor": "outfit-video", "admin_token": admin_token()}) + "\n")
        f.write(json.dumps({**op, "as": CHARACTER}) + "\n")
        f.flush()
        s.settimeout(30)
        for line in f:
            msg = json.loads(line)
            if msg.get("op") in (op["op"], "error"):
                return msg
    return {"ok": False, "error": "no answer"}


def encode(src_frames: Path, caption: str, out: Path, fps: float, crf: int, crop: str | None = None) -> str | None:
    cap = out.with_suffix(".txt")
    cap.write_text(caption, encoding="utf-8")
    vf = (f"drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':textfile='{cap.name}':x=16:y=h-40:fontsize=22:"
          "fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=8")
    if crop:
        vf = f"crop={crop}," + vf
    r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", f"{fps:.3f}", "-i", str(src_frames / "%04d.jpg"),
                        "-vf", vf, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", str(crf), "-preset", "slow",
                        str(out.name)], capture_output=True, text=True, cwd=out.parent)
    return None if r.returncode == 0 else r.stderr[-400:]


def concat(parts: list[Path], out: Path) -> str | None:
    lst = out.with_suffix(".list")
    lst.write_text("".join(f"file '{p.name}'\n" for p in parts), encoding="utf-8")
    r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", lst.name,
                        "-c", "copy", "-movflags", "+faststart", out.name], capture_output=True, text=True, cwd=out.parent)
    return None if r.returncode == 0 else r.stderr[-400:]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--stage", type=Path, required=True)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--frames", type=int, default=70, help="frames each of the eight directions is held")
    ap.add_argument("--fps", type=int, default=15, help="the capture rate asked for (the real one is measured)")
    ap.add_argument("--discord-crf", type=int, default=28)
    args = ap.parse_args()
    cfg = load_config()
    stage = args.stage.resolve()
    ids = json.loads((stage / "astral_ids.json").read_text(encoding="utf-8"))
    out = (args.out or cfg.build / "outfit_video" / time.strftime("%Y%m%d-%H%M%S")).resolve()
    watch = out / "watch"
    watch.mkdir(parents=True, exist_ok=True)
    print(f"[outfit] output: {out}", flush=True)
    report: dict = {"stage": str(stage), "ids": ids, "steps": []}

    shard = str(cfg.tools / "editor_shard" / "run.py")
    sh(shard, "stop")
    if sh(shard, "bridge") != 0 or sh(shard, "start", "--data-first", str(stage)) != 0:
        return 2

    home = out / "client_home"
    (home / "cache").mkdir(parents=True, exist_ok=True)
    (home / "profiles").mkdir(exist_ok=True)
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles"),
                                                    "files_override": str(stage / "files_override.txt")}), encoding="utf-8")
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True, "default_scale": 0.5}), encoding="utf-8")
    cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--silent",
           "--window-size", "1024,768", "--objects-watch", str(watch),
           "--shard-command", "[self set map felucca", "--shard-command", "[go 1164 1668", "--shard-command", "[where"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
    env.pop("UO_CUSTOM_DATA", None)
    client = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                              stderr=subprocess.STDOUT, env=env, **no_activate())
    segment_seconds = 8 * (args.frames + 12) / 60 + 2.5
    ok = True
    try:
        if not wait_for(lambda: (watch / "watching").exists(), 300):
            print("[outfit] the client never reached its watch")
            return 1
        time.sleep(3)
        r = bridge({"op": "equip-many", "item_ids": [ids[k]["item"] for k in WEAR_ORDER]})
        report["steps"].append({"wear": list(WEAR_ORDER), "reply": r})
        print(f"[outfit] wear all: ok={r.get('ok')}", flush=True)
        ok &= bool(r.get("ok"))
        time.sleep(3)
        (watch / "worn.request").write_text("", encoding="utf-8")
        wait_for(lambda: (watch / "worn.json").exists(), 30)
        time.sleep(0.5)
        worn = json.loads((watch / "worn.json").read_text(encoding="utf-8")).get("worn", [])
        report["worn_seen_by_client"] = [o["graphic"] for o in worn]
        (watch / "doll.paperdoll").write_text("", encoding="utf-8")
        time.sleep(2)
        for i, (off, caption) in enumerate(SEGMENTS):
            if off:
                r = bridge({"op": "unequip", "item_ids": [ids[off]["item"]]})
                report["steps"].append({"off": off, "reply": r})
                print(f"[outfit] {off} off: ok={r.get('ok')} {r.get('removed')}", flush=True)
                ok &= bool(r.get("ok"))
                time.sleep(1.5)
            bridge({"op": "command", "text": START})
            time.sleep(1.0)
            (watch / f"doll{i}.paperdoll").write_text("", encoding="utf-8")
            time.sleep(1.0)
            name = f"seg{i}"
            (watch / f"{name}.rec").write_text(f"{segment_seconds:.1f} {args.fps} jpg", encoding="utf-8")
            time.sleep(0.8)
            (watch / f"{name}.walk8").write_text(str(args.frames), encoding="utf-8")
            if not wait_for(lambda: (watch / f"{name}.walked").exists() and (watch / f"{name}.recorded").exists(),
                            segment_seconds + 60):
                print(f"[outfit] segment {i} did not finish")
                ok = False
            report["steps"].append({"segment": i, "caption": caption,
                                    "walked": (watch / f"{name}.walked").read_text() if (watch / f"{name}.walked").exists() else None})
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        time.sleep(3)
        if client.poll() is None:
            client.kill()
        sh(shard, "stop")

    parts_master, parts_discord = [], []
    for i, (_, caption) in enumerate(SEGMENTS):
        frames = watch / f"seg{i}"
        recorded = watch / f"seg{i}.recorded"
        if not frames.is_dir() or not recorded.exists():
            continue
        n, secs = recorded.read_text().split()
        fps = int(n) / float(secs)  # the rate the frames were really taken at
        report.setdefault("capture_fps", []).append(round(fps, 2))
        for crf, crop, parts, tag in ((22, None, parts_master, "m"), (args.discord_crf, DISCORD_CROP, parts_discord, "d")):
            p = out / f"seg{i}_{tag}.mp4"
            err = encode(frames, caption, p, fps, crf, crop)
            if err:
                print(f"[outfit] ffmpeg: {err}")
                ok = False
            parts.append(p)
    master, discord = out / "astral_wayfarer_master.mp4", out / "astral_wayfarer_discord.mp4"
    for parts, dst in ((parts_master, master), (parts_discord, discord)):
        err = concat(parts, dst)
        if err:
            print(f"[outfit] concat: {err}")
            ok = False
    # Discord takes 10 MB: two passes at the bitrate that fits 9.3 MB.
    if discord.exists() and discord.stat().st_size > 9.5 * 2**20:
        dur = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0",
                                    str(discord)], capture_output=True, text=True).stdout.strip() or 0)
        kbps = int(9.3 * 8 * 1024 / max(dur, 1))
        tmp = out / "astral_wayfarer_discord_2pass.mp4"
        for n in (1, 2):
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(discord), "-c:v", "libx264", "-b:v", f"{kbps}k",
                            "-pass", str(n), "-passlogfile", str(out / "x264pass"), "-preset", "slow", "-pix_fmt", "yuv420p",
                            "-movflags", "+faststart"] + (["-f", "mp4", "NUL"] if n == 1 else [str(tmp)]), cwd=out)
        if tmp.exists():
            tmp.replace(discord)
            report["discord_kbps"] = kbps
    report["master"] = str(master)
    report["discord"] = str(discord)
    report["sizes_mb"] = {p.name: round(p.stat().st_size / 2**20, 2) for p in (master, discord) if p.exists()}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("worn_seen_by_client", "capture_fps", "sizes_mb") if k in report}, indent=1))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
