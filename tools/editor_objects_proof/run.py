#!/usr/bin/env python3
"""Prove a world project's world objects reach a client: export, sync on the shard, look.

Editor phase 6 (ADR-0014), on the private ModernUO instance only (127.0.0.1:2594):

    python tools/editor_objects_proof/run.py --project DIR [--out DIR] [--headless]
    python tools/editor_objects_proof/run.py --live [--out DIR] [--headless]

--live (the live tier): no export and no restart. The shard starts with the
bridge and GUO's old objects cleared, a client logs in and stays, and an
editor (headless, --guo-editor-live objects) on the bridge places an anvil
and a Horse spawner, moves the anvil, then deletes both, through the World
tab's object layer. After each step the client dumps what it holds (and,
windowed, photographs it): the objects must appear, move and go without the
client or the shard restarting.

1. tools/world export + verify: the project's shard/objects.json as ModernUO
   files (Spawns JSON, Decoration cfg) plus the manifest.
2. tools/editor_shard: stop, install the bridge, start with --objects on the
   export. The bridge syncs at boot and logs what it added, changed, deleted
   and kept.
3. A GUO client logs in, goes to the first object, and writes the items and
   mobiles within 12 tiles (--objects-dump), then captures its frame. By
   default its window is shown without activation and never takes the
   keyboard (the no-focus path, tools/guo/process.py); --headless skips the
   frame.
4. Checks, against the model: every placed item stands in the client's
   world at its cell with its art and hue; for every spawner, a spawner item
   stands at its cell, and a creature is within its home range when the
   spawned body is known.

Writes build/editor_objects_proof/report.json. Exit 0 when every object was
found, 1 otherwise, 2 when a step could not run. Never touches the shared
dev shard (2593).
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config, worldobjects  # noqa: E402
from guo.process import no_activate  # noqa: E402

PORT = 2594
SPAWNER_GRAPHIC = "0x1F13"


def sh(*args: str) -> int:
    return subprocess.run([sys.executable, *args]).returncode


def wait_for(pred, timeout: float) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--project", type=Path, help="world project with shard/objects.json (export mode)")
    ap.add_argument("--live", action="store_true", help="live mode: the editor edits a running shard")
    ap.add_argument("--out", type=Path)
    ap.add_argument("--headless", action="store_true", help="no client window, so no frame; the dump still runs")
    args = ap.parse_args()

    cfg = load_config()
    tools = cfg.tools
    if args.live:
        return live(cfg, args)
    if args.project is None:
        print("[objects_proof] --project is needed (or --live)")
        return 2
    project = args.project.resolve()
    objects = worldobjects.load(project)
    if not objects:
        print(f"[objects_proof] {project} has no world objects")
        return 2
    out = (args.out or cfg.build / "editor_objects_proof").resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    export = out / "export"

    # 1. Export and verify.
    if sh(str(tools / "world" / "run.py"), "export", "--project", str(project), "--out", str(export)) != 0:
        return 2
    if sh(str(tools / "world" / "run.py"), "verify", "--project", str(project), "--out", str(export)) != 0:
        return 1

    # 2. The private shard, restarted with the objects.
    shard_log = cfg.build / "shard_private" / "shard.log"
    sh(str(tools / "editor_shard" / "run.py"), "stop")
    if sh(str(tools / "editor_shard" / "run.py"), "bridge") != 0:
        return 2
    if sh(str(tools / "editor_shard" / "run.py"), "start", "--objects", str(export)) != 0:
        return 2

    def synced() -> str | None:
        text = shard_log.read_text(encoding="utf-8", errors="replace") if shard_log.exists() else ""
        lines = [l for l in text.splitlines() if "world objects sync" in l]
        return lines[-1] if lines else None

    if not wait_for(lambda: synced() is not None, 120):
        print("[objects_proof] the bridge never logged a world objects sync")
        return 1
    sync_line = synced()
    print(f"[objects_proof] shard: {sync_line}")

    # 3. The client, at the first object.
    first = (objects.items or objects.spawners)[0]
    home = out / "client_home"
    (home / "cache").mkdir(parents=True)
    (home / "profiles").mkdir()
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    # A fresh profile starts from default.json: no top bar over the world.
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
    dump = out / "client_objects.json"
    cmd = [str(cfg.godot_console_exe), *(["--headless"] if args.headless else []), "--path", str(cfg.godot_project), "--",
           "--play", "--window-size", "1024,768", "--screenshot-dir", str(out), "--screenshot-name", "client",
           "--objects-dump", str(dump),
           "--shard-command", f"[self set map {first.map.lower()}",
           "--shard-command", f"[go {first.x - 1} {first.y + 1}",
           "--shard-command", "[where", "--shard-command", "[where", "--shard-command", "[where"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
    with (out / "client.log").open("w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, **no_activate())
        # Headless there is no frame to capture, and the capture would wait for
        # one forever: the dump is the evidence, so end the client once it exists.
        if args.headless:
            wait_for(lambda: dump.is_file() or proc.poll() is not None, 300)
            time.sleep(1)
            if proc.poll() is None:
                proc.kill()
        try:
            proc.wait(timeout=600)
        except subprocess.TimeoutExpired:
            proc.kill()
        code = proc.returncode
    if not dump.is_file():
        print(f"[objects_proof] the client wrote no objects dump (exit {code}); see {out / 'client.log'}")
        return 1

    # 4. Check the client's world against the model.
    seen = json.loads(dump.read_text(encoding="utf-8"))
    facet = worldobjects.MAP_NAMES.index(first.map)
    results = []
    for i in objects.items:
        hit = any(int(o["graphic"], 16) == i.item_id and o["x"] == i.x and o["y"] == i.y and o["z"] == i.z
                  and int(o["hue"], 16) == i.hue for o in seen["items"])
        results.append({"kind": "item", "id": i.id, "what": f"0x{i.item_id:04X} at {i.x},{i.y},{i.z}", "found": hit})
    for s in objects.spawners:
        marker = any(o["graphic"] == SPAWNER_GRAPHIC and o["x"] == s.x and o["y"] == s.y for o in seen["items"])
        near = [m for m in seen["mobiles"] if abs(m["x"] - s.x) <= max(s.home_range, 1) + 2
                and abs(m["y"] - s.y) <= max(s.home_range, 1) + 2]
        results.append({"kind": "spawner", "id": s.id, "what": f"{s.entries[0]['name']} at {s.x},{s.y},{s.z}",
                        "found": marker, "spawner_item": marker, "creatures_near": [m["name"] for m in near]})
    shots = sorted(out.glob("client*.png"))
    report = {"project": str(project), "export": str(export), "shard_sync": sync_line, "client_exit": code,
              "client_map": seen.get("map"), "expected_map": facet, "player": seen.get("player"),
              "results": results, "frame": str(shots[-1]) if shots else None}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for r in results:
        extra = f", creatures near: {', '.join(r['creatures_near']) or 'none'}" if r["kind"] == "spawner" else ""
        print(f"[objects_proof]   {'ok  ' if r['found'] else 'FAIL'} {r['kind']:<7} {r['what']}{extra}")
    print(f"[objects_proof] frame: {report['frame'] or 'none (headless or no capture)'}")
    ok = all(r["found"] for r in results) and seen.get("map") == facet
    print("[objects_proof] OK" if ok else "[objects_proof] FAILED")
    return 0 if ok else 1


LIVE_ITEM = (1167, 1666)
LIVE_MOVED = (1166, 1670)
LIVE_SPAWNER = (1162, 1669)
ANVIL = "0x0FAF"


def live(cfg, args) -> int:
    tools = cfg.tools
    out = (args.out or cfg.build / "editor_objects_proof_live").resolve()
    if out.exists():
        shutil.rmtree(out)
    watch = out / "watch"
    watch.mkdir(parents=True)
    shard_log = cfg.build / "shard_private" / "shard.log"

    sh(str(tools / "editor_shard" / "run.py"), "stop")
    if sh(str(tools / "editor_shard" / "run.py"), "bridge") != 0:
        return 2
    if sh(str(tools / "editor_shard" / "run.py"), "start", "--clear-objects") != 0:
        return 2

    procs = {}
    try:
        # The client: logs in, stands by the spot, and answers dump (and frame) requests.
        home = out / "client_home"
        (home / "cache").mkdir(parents=True)
        (home / "profiles").mkdir()
        (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
        (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
        cmd = [str(cfg.godot_console_exe), *(["--headless"] if args.headless else []), "--path", str(cfg.godot_project), "--",
               "--play", "--window-size", "1024,768", "--screenshot-dir", str(out), "--screenshot-name", "client_end",
               "--objects-watch", str(watch),
               "--shard-command", "[self set map felucca", "--shard-command", "[go 1164 1668",
               "--shard-command", "[where"]
        env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
               "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
        procs["client"] = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                                           stderr=subprocess.STDOUT, env=env, **no_activate())

        # The editor, headless, on the bridge.
        ed = out / "editor"
        ed.mkdir()
        ecmd = [str(cfg.godot_console_exe), "--headless", "--editor", "--path", str(cfg.godot_project), "--",
                "--guo-editor-smoke", str(ed), "--guo-editor-live", "objects", "--guo-editor-live-port", "2595"]
        eenv = {**os.environ, "UO_WORLD_PROJECT": str(ed / "boot_project")}
        procs["editor"] = subprocess.Popen(ecmd, stdout=(out / "editor.log").open("w", encoding="utf-8", errors="replace"),
                                           stderr=subprocess.STDOUT, env=eenv, **no_activate())

        if not wait_for(lambda: (watch / "watching").exists(), 240):
            print("[objects_proof] the client never reached its watch")
            return 1
        if not wait_for(lambda: (ed / "objects.ready").exists(), 240):
            print("[objects_proof] the editor never connected to the bridge")
            return 1
        cleared = [l for l in shard_log.read_text(encoding="utf-8", errors="replace").splitlines() if "world objects sync" in l]

        def look(name: str) -> dict:
            (watch / f"{name}.request").write_text("", encoding="utf-8")
            if not args.headless:
                (watch / f"{name}.shot").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / f"{name}.json").exists() and (args.headless or (watch / f"{name}.png").exists()), 60)
            time.sleep(0.5)
            return json.loads((watch / f"{name}.json").read_text(encoding="utf-8"))

        def step(name: str) -> list:
            t0 = time.time()
            (ed / name).write_text("", encoding="utf-8")
            if not wait_for(lambda: (ed / f"{name}.done").exists(), 90):
                raise RuntimeError(f"the editor never finished {name}")
            acks = json.loads((ed / f"{name}.done").read_text(encoding="utf-8"))
            time.sleep(2)   # the server's packets reach the client
            return [acks, round(time.time() - t0, 2)]

        def items_at(d, graphic, cell):
            return [o for o in d["items"] if o["graphic"] == graphic and (o["x"], o["y"]) == cell]

        before = look("before")
        put, put_s = step("put")
        after_put = look("after_put")
        move, move_s = step("move")
        after_move = look("after_move")
        delete, delete_s = step("delete")
        after_delete = look("after_delete")

        checks = {
            "baseline_clear": not items_at(before, ANVIL, LIVE_ITEM) and not items_at(before, SPAWNER_GRAPHIC, LIVE_SPAWNER),
            "put_anvil_seen": bool(items_at(after_put, ANVIL, LIVE_ITEM)),
            "put_spawner_seen": bool(items_at(after_put, SPAWNER_GRAPHIC, LIVE_SPAWNER)),
            "put_horse_near": any(m["name"].endswith("horse") and abs(m["x"] - LIVE_SPAWNER[0]) <= 4
                                  and abs(m["y"] - LIVE_SPAWNER[1]) <= 4 for m in after_put["mobiles"]),
            "move_anvil_at_new_cell": bool(items_at(after_move, ANVIL, LIVE_MOVED)),
            "move_anvil_gone_from_old": not items_at(after_move, ANVIL, LIVE_ITEM),
            "delete_anvil_gone": not items_at(after_delete, ANVIL, LIVE_MOVED),
            "delete_spawner_gone": not items_at(after_delete, SPAWNER_GRAPHIC, LIVE_SPAWNER),
        }
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        for p in procs.values():
            if p.poll() is None:
                time.sleep(2)
                p.kill()

    bridge = [l for l in shard_log.read_text(encoding="utf-8", errors="replace").splitlines() if "object " in l and "bridge" in l]
    report = {"mode": "live", "baseline_sync": cleared[-1] if cleared else None, "checks": checks,
              "put": {"acks": put, "seconds": put_s}, "move": {"acks": move, "seconds": move_s},
              "delete": {"acks": delete, "seconds": delete_s}, "bridge_log": bridge,
              "frames": [str(p) for p in sorted(watch.glob("*.png"))]}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for line in bridge:
        print(f"[objects_proof] shard: {line}")
    for k, v in checks.items():
        print(f"[objects_proof]   {'ok  ' if v else 'FAIL'} {k}")
    print(f"[objects_proof] steps: put {put_s} s, move {move_s} s, delete {delete_s} s (editor to shard ack, plus 2 s)")
    print(f"[objects_proof] frames: {', '.join(p.name for p in sorted(watch.glob('*.png'))) or 'none (headless)'}")
    ok = all(checks.values())
    print("[objects_proof] OK" if ok else "[objects_proof] FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
