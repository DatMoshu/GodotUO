"""Prove an authored multi, or a scene of them, in game: the private shard, a client, a walk.

1. The private ModernUO (tools/editor_shard, this checkout's copy and ports) starts
   with the stage first in its data directories, so it reads the staged multis.
2. A flat, empty site is found near the probe character's spot (land within 2 z,
   no statics, no water), unless --at X Y gives one.
3. A client logs in with the stage's files_override (so it draws the staged
   multi), auto_open_doors and smooth_doors on. Its window never takes the focus.
4. The bridge's "multi" op places the multi and its doors (a scene: every part, at
   the site plus the part's centre).
5. The character walks (the client's own pathfinder, the watch's .goto) the stops
   the generator wrote (through the yard's gate, up the step, in, up each stair, out
   onto a balcony), or a scene's tour, and each extra "--visit X Y Z" (local). Every
   stop is a frame and a world dump; the report holds where the client says the
   player stood, and a stop counts only at its x and y and within 4 of its z.
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


def occupancy(cfg, facet: int = 0):
    """Per cell of the facet (west of the dungeons): land z, and whether a house cannot stand
    there (water, impassable land, or any static). Cached under build/multi, keyed by the
    map files' sizes and times."""
    import numpy as np
    maps = sorted(Path(cfg.client_data).glob(f"*map{facet}*")) + sorted(Path(cfg.client_data).glob(f"*statics{facet}*"))
    key = "|".join(f"{p.name}:{p.stat().st_size}:{int(p.stat().st_mtime)}" for p in maps)
    cache = cfg.build / "multi" / f"occupancy{facet}.npz"
    if cache.exists():
        d = np.load(cache)
        if str(d["key"]) == key:
            return d["z"], d["blocked"]
    td = TileData(cfg.client_data)
    wet = {}
    with open_facet(cfg.client_data, facet) as f:
        w, h = (min(f.width_blocks * 8, 5120) if facet in (0, 1) else f.width_blocks * 8), f.height_blocks * 8
        z = np.zeros((h, w), np.int8)
        blocked = np.zeros((h, w), bool)
        for bx in range(w >> 3):
            for by in range(h >> 3):
                b = f.read(bx, by)
                zs = np.array(b.land_z, np.int8).reshape(8, 8)
                ids = b.land_id
                bl = np.zeros(64, bool)
                for i in range(64):
                    v = wet.get(ids[i])
                    if v is None:
                        land = td.land(ids[i]) or {"flags": 0}
                        v = wet[ids[i]] = bool(land["flags"] & (WET | IMPASSABLE))
                    bl[i] = v
                for s in b.statics:
                    bl[s[2] * 8 + s[1]] = True
                z[by * 8:by * 8 + 8, bx * 8:bx * 8 + 8] = zs
                blocked[by * 8:by * 8 + 8, bx * 8:bx * 8 + 8] = bl.reshape(8, 8)
    cache.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(cache, z=z, blocked=blocked, key=np.array(key))
    return z, blocked


def find_site(cfg, w: int, h: int, near=START, radius: int = 600) -> tuple[int, int, int] | None:
    """The nearest (x, y, z) whose w x h box (plus a tile of margin) is land within 2 z with no
    statics and no water."""
    import numpy as np
    z, blocked = occupancy(cfg)
    x0, y0 = max(near[0] - radius, 1), max(near[1] - radius, 1)
    x1, y1 = min(near[0] + radius, z.shape[1] - w - 2), min(near[1] + radius, z.shape[0] - h - 2)
    sub = blocked[y0 - 1:y1 + h + 2, x0 - 1:x1 + w + 2].astype(np.int32)
    sat = np.pad(sub.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    bw, bh = w + 3, h + 3                                        # the box plus a tile each side
    cnt = sat[bh:, bw:] - sat[:-bh, bw:] - sat[bh:, :-bw] + sat[:-bh, :-bw]
    ys, xs = np.nonzero(cnt == 0)
    if not len(xs):
        return None
    cx, cy = xs + x0, ys + y0                                    # the box's top-left corner
    order = np.argsort(np.abs(cx - near[0]) + np.abs(cy - near[1]))
    for i in order[:5000]:
        bx, by = int(cx[i]), int(cy[i])
        patch = z[by - 1:by + h + 2, bx - 1:bx + w + 2]
        if int(patch.max()) - int(patch.min()) <= 2:
            return bx, by, int(patch.min())
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
    """One multi: place it on a clear site and walk the generator's stops (or the old door walk)."""
    index = json.loads((stage / "multis.json").read_text(encoding="utf-8"))
    if name not in index:
        print(f"[prove] {name} is not in {stage / 'multis.json'}; write it first")
        return 2
    m = index[name]
    w, h = m["size"]
    bx0, by0, bx1, by1 = m.get("bounds") or [-(w // 2), -(h // 2), w - w // 2, h - h // 2]
    site = pick_site(cfg, at, bx0, by0, bx1, by1)
    if site is None:
        return 1
    if m.get("stops"):
        # the generator's own walk: through the yard's gate, up the step, in, up each stair
        stops = [(t["name"], t["x"], t["y"], t["z"]) for t in m["stops"]]
    else:
        door = m["doors"][0]
        side = "S" if door["facing"] == "WestCW" else "E"
        fx, fy = (door["x"], door["y"] + 1) if side == "S" else (door["x"] + 1, door["y"])
        floor = m["storeys"][0]
        out_x, out_y = (fx, fy + 2) if side == "S" else (fx + 2, fy)
        stops = [("front", out_x, out_y, 0), ("step", fx, fy, floor - 5),
                 ("doorway", door["x"], door["y"], floor), ("inside", 0, 0, floor)]
    stops += [(f"visit{n}", v[0], v[1], v[2]) for n, v in enumerate(visits)]
    print(f"[prove] {name} = multi {m['id']:#06x}, {w}x{h}, at {site}", flush=True)
    return session(cfg, stage, out, [(name, m["id"], 0, 0, m["doors"])], site, stops, clip, min_free_gb,
                   caption or f"GUO: an authored multi ({name}), walked through in game", {"multi": name})


def prove_scene(cfg, name: str, stage: Path, out: Path, clip: Path | None, at=None, min_free_gb: float = 16,
                caption: str = "") -> int:
    """A scene: every part placed at the site plus its centre, then the scene's tour walked."""
    scenes = json.loads((stage / "scenes.json").read_text(encoding="utf-8")) if (stage / "scenes.json").exists() else {}
    if name not in scenes:
        print(f"[prove] no scene {name} in {stage / 'scenes.json'}; scene-write it first")
        return 2
    sc = scenes[name]
    bx0, by0, bx1, by1 = sc["bounds"]
    site = pick_site(cfg, at, bx0, by0, bx1, by1)
    if site is None:
        return 1
    parts = [(f"{name}.{p['name']}", p["id"], p["centre"][0], p["centre"][1], p.get("doors", [])) for p in sc["parts"]]
    stops = [(t["name"], t["x"], t["y"], t["z"]) for t in sc.get("tour", [])]
    if any(t.get("on_land") for t in sc.get("tour", [])):
        # a stop on bare land: the walker stands on the land's own z there, not the scene's ground
        z, _ = occupancy(cfg, 0)
        stops = [(n, x, y, int(z[site[1] + y, site[0] + x]) - site[2] if t.get("on_land") else lz)
                 for (n, x, y, lz), t in zip(stops, sc["tour"])]
    print(f"[prove] scene {name}: {len(parts)} multis at {site}", flush=True)
    return session(cfg, stage, out, parts, site, stops, clip, min_free_gb,
                   caption or f"GUO: an authored scene ({name}), walked through in game", {"scene": name})


def prove_world(cfg, export: Path, stops: list, out: Path, clip: Path | None, min_free_gb: float = 16,
                caption: str = "", profile: dict | None = None) -> int:
    """A tools/world export (map edits, no multis): the shard reads it first, the client through
    its files_override, and the tour (name, x, y, z on the map) is walked. UltimaLive clients keep
    a copy of the map per shard name and never refresh it, so the shard takes a name of its own
    for these bytes; a copy this proof made is removed afterwards."""
    import hashlib
    if not (export / "files_override.txt").exists():
        print(f"[prove] {export} is not a tools/world export (no files_override.txt)")
        return 2
    h = hashlib.sha1()
    for f in sorted(export.glob("*.mul")) + sorted(export.glob("*.uop")):
        h.update(f.name.encode())
        h.update(f.read_bytes())
    name = f"GUO-Proof-{h.hexdigest()[:10]}"
    copy = Path(os.environ.get("ProgramData", r"C:\ProgramData")) / name
    made = not copy.exists()
    print(f"[prove] world export {export.name}: {len(stops)} stops, shard name {name}", flush=True)
    try:
        return session(cfg, export, out, [], (0, 0, 0), stops, clip, min_free_gb,
                       caption or "GUO: map edits, walked through in game", {"world": str(export)},
                       shard_env={"GUO_BRIDGE_SHARD": name}, profile=profile)
    finally:
        if made and copy.is_dir() and copy.name.startswith("GUO-Proof-"):
            shutil.rmtree(copy, ignore_errors=True)


def pick_site(cfg, at, bx0, by0, bx1, by1):
    """The world spot for local (0, 0): --at, or a clear flat box round the bounds."""
    if at:
        if len(at) > 2:
            return at[0], at[1], at[2]
        # no z given: stand it on the land most of it covers, so its doors and gates meet the
        # ground a walker comes from; the plinth reaches lower land, higher land is buried
        import numpy as np
        z, _ = occupancy(cfg, 0)
        land = z[at[1] + by0:at[1] + by1 + 1, at[0] + bx0:at[0] + bx1 + 1]
        vals, counts = np.unique(land, return_counts=True)
        most = int(vals[counts.argmax()])
        print(f"[prove] land under the scene: z {int(land.min())}..{int(land.max())}, mostly {most}; standing it at {most}")
        return at[0], at[1], most
    corner = find_site(cfg, bx1 - bx0 + 4, by1 - by0 + 4)
    if corner is None:
        print("[prove] no flat empty site near the start")
        return None
    return corner[0] + 2 - bx0, corner[1] + 2 - by0, corner[2]


def session(cfg, stage: Path, out: Path, parts: list, site, stops: list, clip: Path | None, min_free_gb: float,
            caption: str, report: dict, shard_env: dict | None = None, profile: dict | None = None) -> int:
    """Start the private shard on the stage, place every (tag, id, cx, cy, doors) at the site plus
    (cx, cy), log a client in and walk `stops` (name, local x, y, z), a frame and a dump at each.
    `shard_env` goes to the shard's start, `profile` over the client's profile options."""
    shard = [sys.executable, str(cfg.tools / "editor_shard" / "run.py")]
    gb = free_gb()
    if gb < min_free_gb:
        print(f"[prove] only {gb:.1f} GB free; a shard and a client need {min_free_gb:.0f} GB free. Not started.")
        return 3
    state = json.loads((cfg.build / "shard_private" / "state.json").read_text(encoding="utf-8"))
    port, bport = state["port"], state.get("bridge_port", 2595)
    if port in (2593, 2594):
        print(f"[prove] the private shard here listens on {port}, a shared port; run editor_shard setup --port N")
        return 2
    x, y, z = site
    for scratch in ("watch", "client_home"):
        shutil.rmtree(out / scratch, ignore_errors=True)
    watch = out / "watch"
    watch.mkdir(parents=True)
    subprocess.run([*shard, "stop"])
    if subprocess.run([*shard, "start", "--data-first", str(stage)], env={**os.environ, **(shard_env or {})}).returncode != 0:
        return 2
    report["site"] = [x, y, z]
    report["place"] = {}
    client = None
    try:
        # the shard keeps its world: take down every multi earlier proofs left standing, from any
        # stage (an old stage's multi ids read this stage's data, so one left up is a stranger)
        removed = bridge(bport, {"op": "multi", "action": "remove", "tag": "*"}, "multi_ack").get("removed", 0)
        if removed:
            print(f"[prove] removed {removed} multi(s) left by earlier proofs", flush=True)
        for tag, mid, cx, cy, doors in parts:
            ack = bridge(bport, {"op": "multi", "action": "place", "tag": tag, "id": mid, "map": 0,
                                 "x": x + cx, "y": y + cy, "z": z, "doors": doors}, "multi_ack")
            report["place"][tag] = ack
            print(f"[prove] place {tag}: ok {ack.get('ok')}, {ack.get('components')} components, "
                  f"{ack.get('doors')} doors {ack.get('error', '')}", flush=True)
            if not ack.get("ok"):
                return 1
            if z is None:
                z = ack["at"][2]
        home = out / "client_home"
        (home / "cache").mkdir(parents=True)
        (home / "profiles").mkdir()
        (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles"),
                                                        "files_override": str(stage / "files_override.txt")}),
                                            encoding="utf-8")
        (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True,
                                                                    "auto_open_doors": True, "smooth_doors": True,
                                                                    **(profile or {})}),
                                                        encoding="utf-8")
        ax, ay = x + stops[0][1], y + stops[0][2]
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
            want = [x + lx, y + ly, z + lz]
            # a long walk can stop short (a door swinging, the pathfinder's own reach): walk
            # on from there, as a player clicks again, up to three times
            for tries in range(1, 4):
                (watch / f"{tag}.arrived").unlink(missing_ok=True)
                (watch / f"{tag}.goto").write_text(" ".join(map(str, want)), encoding="utf-8")
                wait_for(lambda: (watch / f"{tag}.arrived").exists(), 180)
                time.sleep(0.8)
                words = (watch / f"{tag}.arrived").read_text(encoding="utf-8").split() if (watch / f"{tag}.arrived").exists() else []
                where = [int(v) for v in words[-3:]] if len(words) >= 3 and words[-1].lstrip("-").isdigit() else None
                # x and y exactly, z within a step: a stair's top or a threshold can differ by a few
                arrived = where is not None and where[:2] == want[:2] and abs(where[2] - want[2]) <= 4
                if arrived or where is None or where[:2] == want[:2]:
                    break
            stop = {"target": want, "local": [lx, ly, lz], "client_says": " ".join(words), "arrived": arrived,
                    "tries": tries}
            print(f"[prove] {tag}: {stop}", flush=True)
            look(tag)
            return stop

        report["start"] = look("start").get("player")
        if clip:
            (watch / "walk.rec").write_text(f"{min(40 + 12 * len(stops), 240)} 8 jpg", encoding="utf-8")
            time.sleep(1.0)
        report["stops"] = {t: goto(t, lx, ly, lz) for t, lx, ly, lz in stops}
        if clip:
            (watch / "walk.stop").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / "walk.recorded").exists(), 300)
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        time.sleep(2)
        if client is not None and client.poll() is None:
            client.kill()
        subprocess.run([*shard, "stop"])

    if clip and (watch / "walk").is_dir():
        frames = sorted((watch / "walk").glob("*.jpg")) or sorted((watch / "walk").glob("*.png"))
        ext = frames[0].suffix if frames else ".png"
        (out / "caption.txt").write_text(caption, encoding="utf-8")
        font = "C\\:/Windows/Fonts/arial.ttf"    # ffmpeg's escape for the drive colon, once: no shell in between
        vf = (f"drawtext=fontfile='{font}':textfile=caption.txt:x=16:y=h-36:fontsize=20:fontcolor=white:"
              "box=1:boxcolor=black@0.55:boxborderw=6")
        clip.parent.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", "8", "-i", str(watch / "walk" / f"%04d{ext}"),
                            "-vf", vf, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "26", "-movflags", "+faststart",
                            str(clip.resolve())], capture_output=True, text=True, cwd=out)
        report["clip"] = str(clip) if r.returncode == 0 else f"ffmpeg failed: {r.stderr[-300:]}"
    (out / "report.json").write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
    ok = all(a.get("ok") for a in report["place"].values()) and all(s["arrived"] for s in report.get("stops", {}).values())
    print(f"[prove] {'PASS' if ok else 'FAIL'}: {out}")
    return 0 if ok else 1
