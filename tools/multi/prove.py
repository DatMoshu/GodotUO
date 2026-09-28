"""Prove an authored multi in game: the private shard, a client, a walk through the door.

1. The private ModernUO (tools/editor_shard, this checkout's copy and ports) starts
   with the stage first in its data directories, so it reads the staged multis.
2. A flat, empty site is found near the probe character's spot (land within 2 z,
   no statics, no water), unless --at X Y gives one.
3. A client logs in with the stage's files_override (so it draws the staged
   multi), auto_open_doors and smooth_doors on. Its window never takes the focus.
4. The bridge's "multi" op places the multi and its doors.
5. The character walks (the client's own pathfinder, the watch's .goto): to the
   front, onto the step, through the door, into the middle of the ground floor,
   and for each extra "--visit X Y Z" (multi-local), there too. Every stop is a
   frame and a world dump; the report holds where the client says the player stood.
6. With --clip the walk is recorded and written as an MP4.

Never the shared shard; never with less than --min-free-gb of memory free.
"""
from __future__ import annotations

import json
import os
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.process import no_activate  # noqa: E402
from guo.uomap import open_facet  # noqa: E402
from guo.uoread import TileData  # noqa: E402

START = (1164, 1668)          # the probe character's spot in the private shard's copied saves
WET, IMPASSABLE = 0x80, 0x40


def free_gb() -> float:
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB"],
                         capture_output=True, text=True).stdout.strip()
    return float(out or 0)


def find_site(cfg, w: int, h: int, near=START, radius: int = 60) -> tuple[int, int, int] | None:
    """The nearest (x, y, z) whose w x h box (plus a tile of margin) is flat land with no statics."""
    td = TileData(cfg.client_data)
    with open_facet(cfg.client_data, 0) as f:
        blocks: dict = {}

        def cell(x, y):
            b = blocks.get((x >> 3, y >> 3))
            if b is None:
                b = blocks[(x >> 3, y >> 3)] = f.read(x >> 3, y >> 3)
            return b, (y & 7) * 8 + (x & 7)

        def ok(x0, y0):
            zs = set()
            for x in range(x0 - 1, x0 + w + 2):
                for y in range(y0 - 1, y0 + h + 2):
                    b, i = cell(x, y)
                    land = td.land(b.land_id[i]) or {"flags": 0}
                    if land["flags"] & (WET | IMPASSABLE):
                        return None
                    if any(s[1] == (x & 7) and s[2] == (y & 7) for s in b.statics):
                        return None
                    zs.add(b.land_z[i])
            return min(zs) if max(zs) - min(zs) <= 2 else None

        for r in range(0, radius, 2):
            for dx in range(-r, r + 1, 2):
                for dy in (-r, r) if abs(dx) != r else range(-r, r + 1, 2):
                    z = ok(near[0] + dx, near[1] + dy)
                    if z is not None:
                        return near[0] + dx, near[1] + dy, z
    return None


def bridge(port: int, msg: dict, want: str, timeout: float = 30) -> dict:
    with socket.create_connection(("127.0.0.1", port), timeout=10) as s:
        f = s.makefile("rw", encoding="utf-8", newline="\n")
        f.write(json.dumps({"op": "hello", "editor": "multi-prove"}) + "\n")
        f.write(json.dumps(msg) + "\n")
        f.flush()
        s.settimeout(timeout)
        for line in f:
            m = json.loads(line)
            if m.get("op") in (want, "error"):
                return m
    return {"ok": False, "error": "no answer"}


def wait_for(pred, timeout: float) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


def prove(cfg, name: str, stage: Path, out: Path, clip: Path | None, at=None, visits=(), min_free_gb: float = 16,
          caption: str = "") -> int:
    shard = [sys.executable, str(cfg.tools / "editor_shard" / "run.py")]
    index = json.loads((stage / "multis.json").read_text(encoding="utf-8"))
    if name not in index:
        print(f"[prove] {name} is not in {stage / 'multis.json'}; write it first")
        return 2
    m = index[name]
    gb = free_gb()
    if gb < min_free_gb:
        print(f"[prove] only {gb:.1f} GB free; a shard and a client need {min_free_gb:.0f} GB free. Not started.")
        return 3
    state = json.loads((cfg.build / "shard_private" / "state.json").read_text(encoding="utf-8"))
    port, bport = state["port"], state.get("bridge_port", 2595)
    if port in (2593, 2594):
        print(f"[prove] the private shard here listens on {port}, a shared port; run editor_shard setup --port N")
        return 2
    w, h = m["size"]
    cx, cy = w // 2, h // 2
    if at:
        site = (at[0], at[1], at[2] if len(at) > 2 else None)
    else:
        corner = find_site(cfg, w + 4, h + 4)
        if corner is None:
            print("[prove] no flat empty site near the start")
            return 1
        site = (corner[0] + 2 + cx, corner[1] + 2 + cy, corner[2])
    x, y, z = site
    print(f"[prove] {name} = multi {m['id']:#06x}, {w}x{h}, at {x},{y},{z} on the shard 127.0.0.1:{port}", flush=True)

    for scratch in ("watch", "client_home"):
        shutil.rmtree(out / scratch, ignore_errors=True)
    watch = out / "watch"
    watch.mkdir(parents=True)
    subprocess.run([*shard, "stop"])
    if subprocess.run([*shard, "start", "--data-first", str(stage)]).returncode != 0:
        return 2
    report: dict = {"multi": name, "id": m["id"], "site": [x, y, z]}
    client = None
    try:
        report["place"] = bridge(bport, {"op": "multi", "action": "place", "tag": name, "id": m["id"], "map": 0,
                                         "x": x, "y": y, "z": z, "doors": m["doors"]}, "multi_ack")
        print(f"[prove] place: {report['place']}", flush=True)
        if not report["place"].get("ok"):
            return 1
        z = report["place"]["at"][2]
        door = m["doors"][0]
        side = "S" if door["facing"] == "WestCW" else "E"
        fx, fy = (door["x"], door["y"] + 1) if side == "S" else (door["x"] + 1, door["y"])
        home = out / "client_home"
        (home / "cache").mkdir(parents=True)
        (home / "profiles").mkdir()
        (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles"),
                                                        "files_override": str(stage / "files_override.txt")}),
                                            encoding="utf-8")
        (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True,
                                                                    "auto_open_doors": True, "smooth_doors": True}),
                                                        encoding="utf-8")
        ax, ay = (x + fx, y + fy + 4) if side == "S" else (x + fx + 4, y + fy)
        cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--window-size", "1024,768",
               "--screenshot-dir", str(out), "--screenshot-name", "end", "--objects-watch", str(watch),
               "--shard-command", "[self set map felucca", "--shard-command", f"[go {ax} {ay}",
               "--shard-command", "[where"]
        env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
               "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(port)}
        client = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                                  stderr=subprocess.STDOUT, env=env, **no_activate())
        if not wait_for(lambda: (watch / "watching").exists(), 300):
            print("[prove] the client never reached its watch")
            return 1
        time.sleep(4)

        def look(tag: str) -> dict:
            (watch / f"{tag}.request").write_text("", encoding="utf-8")
            (watch / f"{tag}.shot").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / f"{tag}.json").exists() and (watch / f"{tag}.png").exists(), 60)
            time.sleep(0.3)
            d = json.loads((watch / f"{tag}.json").read_text(encoding="utf-8"))
            shutil.copy(watch / f"{tag}.png", out / f"{tag}.png")
            return d

        def goto(tag: str, lx: int, ly: int, lz: int) -> dict:
            (watch / f"{tag}.goto").write_text(f"{x + lx} {y + ly} {z + lz}", encoding="utf-8")
            wait_for(lambda: (watch / f"{tag}.arrived").exists(), 120)
            time.sleep(0.8)
            words = (watch / f"{tag}.arrived").read_text(encoding="utf-8").split() if (watch / f"{tag}.arrived").exists() else []
            where = [int(v) for v in words[-3:]] if len(words) >= 3 and words[-1].lstrip("-").isdigit() else None
            want = [x + lx, y + ly, z + lz]
            arrived = where is not None and where[:2] == want[:2]
            stop = {"target": want, "local": [lx, ly, lz], "client_says": " ".join(words), "arrived": arrived}
            print(f"[prove] {tag}: {stop}", flush=True)
            look(tag)
            return stop

        report["start"] = look("start").get("player")
        if clip:
            (watch / "walk.rec").write_text("40 8 jpg", encoding="utf-8")
            time.sleep(1.0)
        floor = m["storeys"][0]
        out_x, out_y = (fx, fy + 2) if side == "S" else (fx + 2, fy)
        stops = [("front", out_x, out_y, 0),
                 ("step", fx, fy, floor - 5),
                 ("doorway", door["x"], door["y"], floor),
                 ("inside", 0, 0, floor)]
        stops += [(f"visit{n}", v[0], v[1], v[2]) for n, v in enumerate(visits)]
        report["stops"] = {t: goto(t, lx, ly, lz) for t, lx, ly, lz in stops}
        if clip:
            wait_for(lambda: (watch / "walk.recorded").exists(), 90)
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        time.sleep(2)
        if client is not None and client.poll() is None:
            client.kill()
        subprocess.run([*shard, "stop"])

    if clip and (watch / "walk").is_dir():
        frames = sorted((watch / "walk").glob("*.jpg")) or sorted((watch / "walk").glob("*.png"))
        ext = frames[0].suffix if frames else ".png"
        (out / "caption.txt").write_text(caption or f"GUO: an authored multi ({name}), walked through in game",
                                         encoding="utf-8")
        font = "C\\:/Windows/Fonts/arial.ttf"    # ffmpeg's escape for the drive colon, once: no shell in between
        vf = (f"drawtext=fontfile='{font}':textfile=caption.txt:x=16:y=h-36:fontsize=20:fontcolor=white:"
              "box=1:boxcolor=black@0.55:boxborderw=6")
        clip.parent.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", "8", "-i", str(watch / "walk" / f"%04d{ext}"),
                            "-vf", vf, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "26", "-movflags", "+faststart",
                            str(clip.resolve())], capture_output=True, text=True, cwd=out)
        report["clip"] = str(clip) if r.returncode == 0 else f"ffmpeg failed: {r.stderr[-300:]}"
    (out / "report.json").write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
    ok = report.get("place", {}).get("ok") and all(s["arrived"] for s in report.get("stops", {}).values())
    print(f"[prove] {'PASS' if ok else 'FAIL'}: {out}")
    return 0 if ok else 1
