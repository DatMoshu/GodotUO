#!/usr/bin/env python3
"""Prove a world project's world objects reach a client: export, sync on the shard, look.

Editor phase 6 (ADR-0014), on the private ModernUO instance only (127.0.0.1:2594):

    python tools/editor_objects_proof/run.py --project DIR [--out DIR] [--headless]
    python tools/editor_objects_proof/run.py --live [--out DIR] [--headless]

--commands (the fallback for a shard without GUO's bridge): the shard starts
WITHOUT the bridge, and tools/world apply-commands has a GM client type the
server's own commands. --project is placed; then a GM places an untagged
decoy anvil on the same cell as the project's (shard content the fallback
must never remove); then --project2 (the edited project) is applied: the
project's anvil moves, its spawner goes, and the decoy must still stand.
Applying --project2 again must type nothing.

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
    ap.add_argument("--clip", type=Path,
                    help="with --live: record the client through the steps and write an MP4 here (ffmpeg)")
    ap.add_argument("--servuo", action="store_true",
                    help="the ServUO backend on the private ServUO shard (tools/servuo, 127.0.0.1:2596)")
    ap.add_argument("--commands", action="store_true", help="GM-command fallback, on the shard without the bridge")
    ap.add_argument("--project2", type=Path, help="the edited project for --commands")
    ap.add_argument("--out", type=Path)
    ap.add_argument("--headless", action="store_true", help="no client window, so no frame; the dump still runs")
    args = ap.parse_args()

    cfg = load_config()
    tools = cfg.tools
    if args.live:
        return live(cfg, args)
    if args.commands:
        return commands_mode(cfg, args)
    if args.servuo:
        return servuo_mode(cfg, args)
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


def gm_client(cfg, out: Path, name: str, commands: list[str], dump: Path | None = None,
              port: int = PORT, character: str | None = None) -> int:
    """A headless GM client on the private shard types commands (and optionally dumps its world), then ends."""
    home = out / f"{name}_home"
    home.mkdir(parents=True)
    (home / "cache").mkdir()
    (home / "profiles").mkdir()
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    cmd = [str(cfg.godot_console_exe), "--headless", "--path", str(cfg.godot_project), "--", "--play"]
    if character:
        cmd += ["--character", character]
    for c in commands:
        cmd += ["--shard-command", c]
    if dump is not None:
        cmd += ["--objects-dump", str(dump)]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(port)}
    log = out / f"{name}.log"
    with log.open("w", encoding="utf-8", errors="replace") as f:
        proc = subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT, env=env, **no_activate())
        wait_for(lambda: (dump is not None and dump.is_file())
                 or (dump is None and log.read_text(encoding="utf-8", errors="replace").count("[GUO] shard command: [") >= len(commands))
                 or proc.poll() is not None, 300)
        time.sleep(3)
        if proc.poll() is None:
            proc.kill()
    return 0 if (dump is None or dump.is_file()) else 1


SERVUO_PORT = 2596


def servuo_mode(cfg, args) -> int:
    """Export with the ServUO backend, restart ServUO with the files, have a GM load them,
    and check a client there sees the items and the spawner's creature."""
    tools = cfg.tools
    if args.project is None:
        print("[objects_proof] --servuo needs --project")
        return 2
    project = args.project.resolve()
    objects = worldobjects.load(project)
    out = (args.out or cfg.build / "editor_objects_proof_servuo").resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    export = out / "export"
    env_backend = {**os.environ, "UO_SHARD_BACKEND": "servuo"}
    for step in (["export", "--project", str(project), "--out", str(export)],
                 ["verify", "--project", str(project), "--out", str(export)]):
        r = subprocess.run([sys.executable, str(tools / "world" / "run.py"), *step], env=env_backend)
        if r.returncode != 0:
            return 1

    # Restart ServUO with the files beside it (it reads neither at boot; the GM loads them).
    src = tools / "servuo" / "src"
    sh(str(tools / "servuo" / "run.py"), "stop")
    for f in (export / "shard").rglob("*"):
        if f.is_file() and f.parts[len((export / "shard").parts)] in ("XmlSpawner", "Data"):
            dst = src / f.relative_to(export / "shard")
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(f, dst)
    if sh(str(tools / "servuo" / "run.py"), "start") != 0:
        return 2

    first = (objects.items or objects.spawners)[0]
    home = out / "client_home"
    (home / "cache").mkdir(parents=True)
    (home / "profiles").mkdir()
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
    dump = out / "client_objects.json"
    load = [f"[XmlLoad guo-{project.name}.xml"] if objects.spawners else []
    cmd = [str(cfg.godot_console_exe), *(["--headless"] if args.headless else []), "--path", str(cfg.godot_project), "--",
           "--play", "--character", "Guoprobe", "--window-size", "1024,768",
           "--screenshot-dir", str(out), "--screenshot-name", "client", "--objects-dump", str(dump)]
    for c in load + ["[Decorate", f"[self set map {first.map.lower()}", f"[go {first.x - 1} {first.y + 1}",
                     "[where", "[where", "[where"]:
        cmd += ["--shard-command", c]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(SERVUO_PORT)}
    with (out / "client.log").open("w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, **no_activate())
        if args.headless:
            wait_for(lambda: dump.is_file() or proc.poll() is not None, 900)
            time.sleep(1)
            if proc.poll() is None:
                proc.kill()
        try:
            proc.wait(timeout=900)
        except subprocess.TimeoutExpired:
            proc.kill()
    if not dump.is_file():
        print(f"[objects_proof] the client wrote no objects dump; see {out / 'client.log'}")
        return 1
    seen = json.loads(dump.read_text(encoding="utf-8"))
    results = []
    for i in objects.items:
        hit = any(int(o["graphic"], 16) == i.item_id and (o["x"], o["y"], o["z"]) == (i.x, i.y, i.z)
                  and int(o["hue"], 16) == i.hue for o in seen["items"])
        results.append({"kind": "item", "what": f"0x{i.item_id:04X} at {i.x},{i.y},{i.z}", "found": hit})
    for sp in objects.spawners:
        marker = any(o["name"].lower() == f"guo {sp.entries[0]['name']}".lower() for o in seen["items"])
        near = [m["name"] for m in seen["mobiles"] if abs(m["x"] - sp.x) <= sp.home_range + 2 and abs(m["y"] - sp.y) <= sp.home_range + 2]
        want = sp.entries[0]["name"].lower()
        results.append({"kind": "spawner", "what": f"{sp.entries[0]['name']} at {sp.x},{sp.y}", "found": marker,
                        "creature_near": any(want in n.lower() for n in near), "near": near})
    shots = sorted(out.glob("client*.png"))
    report = {"mode": "servuo", "results": results, "frame": str(shots[-1]) if shots else None,
              "commands": load + ["[Decorate"]}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for r in results:
        extra = f", creature near: {r['creature_near']} ({', '.join(r['near']) or 'none'})" if r["kind"] == "spawner" else ""
        print(f"[objects_proof]   {'ok  ' if r['found'] else 'FAIL'} {r['kind']:<7} {r['what']}{extra}")
    print(f"[objects_proof] frame: {report['frame'] or 'none'}")
    ok = all(r["found"] for r in results) and all(r.get("creature_near", True) for r in results)
    print("[objects_proof] OK" if ok else "[objects_proof] FAILED")
    return 0 if ok else 1


def commands_mode(cfg, args) -> int:
    tools = cfg.tools
    if args.project is None or args.project2 is None:
        print("[objects_proof] --commands needs --project and --project2")
        return 2
    servuo = args.servuo
    port = 2596 if servuo else PORT
    character = "Guoprobe" if servuo else None
    out = (args.out or cfg.build / ("editor_objects_proof_commands_servuo" if servuo else "editor_objects_proof_commands")).resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    # Fresh copies: the fallback's record lives in the project, so each run starts from none.
    p1, p2 = out / "project1", out / "project2"
    shutil.copytree(args.project, p1, ignore=shutil.ignore_patterns(".cache", "export", "applied"))
    shutil.copytree(args.project2, p2, ignore=shutil.ignore_patterns(".cache", "export", "applied"))
    o1, o2 = worldobjects.load(p1), worldobjects.load(p2)
    anvil1 = next(i for i in o1.items if i.item_id == 0x0FAF)

    shard_log = cfg.build / "shard_private" / "shard.log"
    bridge_loaded = False
    if servuo:
        # ServUO runs no GUO code at all; start it if it is not up.
        sh(str(tools / "servuo" / "run.py"), "start")
    if not servuo:
        # A clean baseline: one boot WITH the bridge and an empty manifest removes
        # what earlier bridge runs placed; then the shard runs as a plain ModernUO.
        sh(str(tools / "editor_shard" / "run.py"), "stop")
        if sh(str(tools / "editor_shard" / "run.py"), "bridge") != 0:
            return 2
        if sh(str(tools / "editor_shard" / "run.py"), "start", "--clear-objects") != 0:
            return 2
        wait_for(lambda: "world objects sync" in shard_log.read_text(encoding="utf-8", errors="replace"), 120)
        time.sleep(3)
        sh(str(tools / "editor_shard" / "run.py"), "stop")
        if sh(str(tools / "editor_shard" / "run.py"), "start", "--no-bridge") != 0:
            return 2
        bridge_loaded = "GUO editor bridge" in shard_log.read_text(encoding="utf-8", errors="replace")

    def apply(project: Path, name: str) -> tuple[int, str]:
        r = subprocess.run([sys.executable, str(tools / "world" / "run.py"), "apply-commands", "--project", str(project),
                            "--host", "127.0.0.1", "--port", str(port)], capture_output=True, text=True)
        (out / f"{name}.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
        return r.returncode, r.stdout

    def look(name: str, at) -> dict:
        dump = out / f"{name}.json"
        gm_client(cfg, out, name, ["[self set map felucca", f"[go {at[0]} {at[1]}", "[where"], dump, port, character)
        return json.loads(dump.read_text(encoding="utf-8"))

    def anvils(d, cell):
        return [o for o in d["items"] if o["graphic"] == "0x0FAF" and (o["x"], o["y"]) == cell]

    spot = (anvil1.x - 1, anvil1.y + 1)
    code1, _ = apply(p1, "apply1")
    seen1 = look("after_apply1", spot)
    # project2 is the same project, edited: it carries the record of what the
    # fallback placed (a user edits one project; the test keeps two copies).
    shutil.copytree(p1 / "shard" / "applied", p2 / "shard" / "applied", dirs_exist_ok=True)
    # Shard content on the project anvil's cell: an untagged anvil, placed by a GM.
    gm_client(cfg, out, "decoy", ["[self set map felucca", f"[go {anvil1.x} {anvil1.y} {anvil1.z}",
                                  f"[TileXYZ {anvil1.x} {anvil1.y} 1 1 {anvil1.z} Static 4015"], None, port, character)
    code2, _ = apply(p2, "apply2")
    seen2 = look("after_apply2", spot)
    code3, again = apply(p2, "apply3")

    moved = next(i for i in o2.items if i.item_id == 0x0FAF)
    hued = [i for i in o2.items if i.item_id != 0x0FAF]
    sp1 = o1.spawners[0]
    checks = {
        "bridge_not_loaded": not bridge_loaded,
        "apply1_ok": code1 == 0,
        "apply1_anvil_tagged": any(o["name"].lower().startswith("guo-") for o in anvils(seen1, (anvil1.x, anvil1.y))),
        "apply1_spawner": any(o["graphic"] == SPAWNER_GRAPHIC and (o["x"], o["y"]) == (sp1.x, sp1.y) for o in seen1["items"]),
        "apply2_ok": code2 == 0,
        "apply2_anvil_moved": any(o["name"].lower().startswith("guo-") for o in anvils(seen2, (moved.x, moved.y))),
        # The fallback's anvil left the cell; everything still there is shard content (untagged),
        # the decoy included: nothing it did not place was removed.
        "apply2_old_cell_only_decoy": bool(anvils(seen2, (anvil1.x, anvil1.y)))
                                      and not any(o["name"].lower().startswith("guo-") for o in anvils(seen2, (anvil1.x, anvil1.y))),
        "apply1_hued_item_absent": not any(int(o["graphic"], 16) == h.item_id and (o["x"], o["y"]) == (h.x, h.y)
                                           for h in hued for o in seen1["items"]),
        "apply2_spawner_gone": not any(o["graphic"] == SPAWNER_GRAPHIC and (o["x"], o["y"]) == (sp1.x, sp1.y)
                                       for o in seen2["items"]),
        "apply2_hued_item": all(any(int(o["graphic"], 16) == h.item_id and (o["x"], o["y"]) == (h.x, h.y)
                                    and int(o["hue"], 16) == h.hue for o in seen2["items"]) for h in hued),
        "apply3_nothing_to_type": code3 == 0 and "nothing to type" in again,
    }
    old_cell = [{"name": o["name"], "serial": o["serial"]} for o in anvils(seen2, (anvil1.x, anvil1.y))]
    report = {"mode": "commands", "checks": checks, "old_cell_after_apply2": old_cell,
              "commands_apply1": (out / "apply1.txt").read_text(encoding="utf-8").splitlines(),
              "commands_apply2": (out / "apply2.txt").read_text(encoding="utf-8").splitlines()}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for line in report["commands_apply2"]:
        print(f"[objects_proof] apply2: {line}")
    print(f"[objects_proof] old anvil cell after apply2: {old_cell}")
    for k, v in checks.items():
        print(f"[objects_proof]   {'ok  ' if v else 'FAIL'} {k}")
    ok = all(checks.values())
    print("[objects_proof] OK" if ok else "[objects_proof] FAILED")
    return 0 if ok else 1


LIVE_ITEM = (1167, 1666)
LIVE_MOVED = (1166, 1670)
LIVE_SPAWNER = (1162, 1669)
ANVIL = "0x0FAF"


def clear_ultimalive_copies() -> None:
    """The client's UltimaLive map copies for the private shard keep earlier live
    terrain edits; start from the install, as tools/editor_live does."""
    ul = Path(os.environ.get("ProgramData", r"C:\ProgramData")) / "GUO-Editor-Private"
    shutil.rmtree(ul, ignore_errors=True)


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
    clear_ultimalive_copies()

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

        # Only objects that were not there before count: a shard may already
        # hold other anvils or spawners on these cells (other runs, shard content).
        baseline = set()

        def items_at(d, graphic, cell=None):
            return [o for o in d["items"] if o["graphic"] == graphic and o["serial"] not in baseline
                    and (cell is None or (o["x"], o["y"]) == cell)]

        before = look("before")
        baseline = {o["serial"] for o in before["items"]}
        put, put_s = step("put")
        after_put = look("after_put")
        move, move_s = step("move")
        after_move = look("after_move")
        delete, delete_s = step("delete")
        after_delete = look("after_delete")

        checks = {
            "baseline_taken": bool(before.get("player")),
            "put_anvil_seen": bool(items_at(after_put, ANVIL, LIVE_ITEM)),
            "put_spawner_seen": bool(items_at(after_put, SPAWNER_GRAPHIC, LIVE_SPAWNER)),
            "put_horse_near": any(m["name"].endswith("horse") and abs(m["x"] - LIVE_SPAWNER[0]) <= 4
                                  and abs(m["y"] - LIVE_SPAWNER[1]) <= 4 for m in after_put["mobiles"]),
            "move_anvil_at_new_cell": bool(items_at(after_move, ANVIL, LIVE_MOVED)),
            "move_anvil_gone_from_old": not items_at(after_move, ANVIL, LIVE_ITEM),
            "delete_anvil_gone": not items_at(after_delete, ANVIL),
            "delete_spawner_gone": not items_at(after_delete, SPAWNER_GRAPHIC),
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
    if ok and args.clip is not None and not args.headless:
        return 0 if clip(cfg, args, out) else 1
    return 0 if ok else 1


def clip(cfg, args, out: Path) -> bool:
    """A second live pass, recorded: the client's frames through put, move and delete, as an MP4 with captions."""
    tools = cfg.tools
    rec = out / "clip"
    watch = rec / "watch"
    watch.mkdir(parents=True)
    sh(str(tools / "editor_shard" / "run.py"), "stop")
    if sh(str(tools / "editor_shard" / "run.py"), "start", "--clear-objects") != 0:
        return False
    clear_ultimalive_copies()
    procs = {}
    marks = []
    try:
        home = rec / "client_home"
        (home / "cache").mkdir(parents=True)
        (home / "profiles").mkdir()
        (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
        (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
        cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--window-size", "1024,768",
               "--screenshot-dir", str(rec), "--screenshot-name", "end", "--objects-watch", str(watch),
               "--shard-command", "[self set map felucca", "--shard-command", "[go 1164 1668", "--shard-command", "[where"]
        env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
               "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
        procs["client"] = subprocess.Popen(cmd, stdout=(rec / "client.log").open("w", encoding="utf-8", errors="replace"),
                                           stderr=subprocess.STDOUT, env=env, **no_activate())
        ed = rec / "editor"
        ed.mkdir()
        ecmd = [str(cfg.godot_console_exe), "--headless", "--editor", "--path", str(cfg.godot_project), "--",
                "--guo-editor-smoke", str(ed), "--guo-editor-live", "objects", "--guo-editor-live-port", "2595"]
        procs["editor"] = subprocess.Popen(ecmd, stdout=(rec / "editor.log").open("w", encoding="utf-8", errors="replace"),
                                           stderr=subprocess.STDOUT, env={**os.environ, "UO_WORLD_PROJECT": str(ed / "boot")},
                                           **no_activate())
        if not (wait_for(lambda: (watch / "watching").exists(), 240) and wait_for(lambda: (ed / "objects.ready").exists(), 240)):
            print("[objects_proof] clip: client or editor never got ready")
            return False
        time.sleep(2)
        seconds = 16
        (watch / "clip.rec").write_text(f"{seconds} 10", encoding="utf-8")
        t0 = time.time()
        for name, pause in (("put", 3), ("move", 4), ("delete", 4)):
            time.sleep(pause)
            marks.append((name, round(time.time() - t0, 2)))
            (ed / name).write_text("", encoding="utf-8")
            wait_for(lambda: (ed / f"{name}.done").exists(), 60)
        wait_for(lambda: (watch / "clip.recorded").exists(), seconds + 30)
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        for p in procs.values():
            if p.poll() is None:
                time.sleep(2)
                p.kill()

    frames = watch / "clip"
    n = len(list(frames.glob("*.png")))
    if n == 0:
        print("[objects_proof] clip: no frames recorded")
        return False
    captions = {"put": "editor places an anvil and a Horse spawner (live)",
                "move": "editor moves the anvil (live)",
                "delete": "editor deletes both (live)"}
    font = "C\\:/Windows/Fonts/arial.ttf"
    filters = ["drawtext=fontfile='%s':text='GUO editor to a running ModernUO shard, no restart':x=16:y=h-62:"
               "fontsize=20:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=6" % font]
    ends = [m[1] for m in marks[1:]] + [seconds]
    for (name, start), end in zip(marks, ends):
        filters.append("drawtext=fontfile='%s':text='%s':x=16:y=h-32:fontsize=20:fontcolor=yellow:box=1:"
                       "boxcolor=black@0.55:boxborderw=6:enable='between(t,%.2f,%.2f)'" % (font, captions[name], start, end))
    args.clip.parent.mkdir(parents=True, exist_ok=True)
    r = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", "10", "-i", str(frames / "%04d.png"),
                        "-vf", ",".join(filters), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "28",
                        "-movflags", "+faststart", str(args.clip)], capture_output=True, text=True)
    if r.returncode != 0:
        print(f"[objects_proof] clip: ffmpeg failed: {r.stderr[-800:]}")
        return False
    print(f"[objects_proof] clip: {n} frames, marks {marks} -> {args.clip} ({args.clip.stat().st_size // 1024} KB)")
    return True


if __name__ == "__main__":
    sys.exit(main())
