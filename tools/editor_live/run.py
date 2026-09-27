#!/usr/bin/env python3
"""The editor's live tier, checked end to end: two editors and a client.

docs/editor_plan.md phase 4: "two editor instances + a client: edit in one,
appears in the other and in the client within 1 s; log lines as evidence".
Everything runs against the PRIVATE shard (tools/editor_shard), never the
shared dev shard.

    python tools/editor_live/run.py [--export DIR]

1. Installs the editor bridge in the private shard and restarts it with the
   world export (--export, default build/shard_proof_export) first in its
   data directories.
2. Clears the client's UltimaLive copies for the private shard's name
   (C:\\ProgramData\\GUO-Editor-Private), so it starts from the export.
3. Logs a client in to the private shard (this checkout's client, the probe
   account, a scratch home whose files_override is the export's) and has it
   stand at 1164,1668 while it types a few harmless commands.
4. Starts editor B (headless, --guo-editor-live follow) and editor A
   (windowed, --guo-editor-live send). Both connect to the bridge.
5. When the bridge says the client is on UltimaLive, tells A to go: A stamps
   a tree at 1167,1666 through the World tab's editor, which sends the block.
6. Collects: A's send time and the shard's ack, B's receipt time, the bridge's
   log lines, the client's UltimaLive log lines and its frame.

Exit codes: 0 every check passed, 1 a check failed, 2 could not run.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402

SHARD_NAME = "GUO-Editor-Private"
PORT, BRIDGE = 2594, 2595
FACET_NAMES = {0: "Felucca", 1: "Trammel"}
PROBE_CHARACTER = "asdfdsaf"   # the probe account's character in the copied saves


def sh(*args: str) -> int:
    return subprocess.run([sys.executable, *args]).returncode


def wait_for(pred, timeout: float, what: str) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.25)
    print(f"[editor_live] timed out waiting for {what}")
    return False


def map_copy_checks(client_data: Path, folder: Path) -> dict:
    """Every facet the client made a copy of, against the install."""
    out = {}
    for f in range(6):
        if (folder / f"map{f}.mul").is_file():
            out[f"map{f}"] = map_copy_check(client_data, folder / f"map{f}.mul", f)
    out["ok"] = bool(out) and all(v["ok"] for v in out.values())
    return out


def map_copy_check(client_data: Path, copy: Path, facet: int = 0) -> dict:
    """The client's UltimaLive map copy against the install's map, block by block.

    Live pushes rewrite blocks with the shard's land, which for this test is
    the install's (the stamp only adds a static), so every block must match.
    A blank copy, or one shifted by a missing UOP entry, fails here.
    """
    from guo.uomap import MAP_BLOCK, open_facet

    if not copy.is_file():
        return {"ok": False, "why": f"no copy at {copy}"}
    raw = copy.read_bytes()
    with open_facet(client_data, facet) as inst:
        n = inst.width_blocks * inst.height_blocks
        if len(raw) != n * MAP_BLOCK:
            return {"ok": False, "why": f"{len(raw)} bytes, the map has {n * MAP_BLOCK}"}
        # The 4-byte block header is not the land; compare the 64 cells.
        diff = sum(1 for b in range(n)
                   if raw[b * MAP_BLOCK + 4:(b + 1) * MAP_BLOCK]
                   != inst.map[inst.map_offset(b) + 4:inst.map_offset(b) + MAP_BLOCK])
    return {"ok": diff == 0, "blocks": n, "blocks_differing": diff, "bytes": len(raw)}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--export", type=Path)
    ap.add_argument("--facet", type=int, default=0, choices=[0, 1],
                    help="map to edit live: 0 Felucca (default) or 1 Trammel, which shares its layout, so the "
                         "same wilderness cell is used on both")
    ap.add_argument("--no-export", action="store_true",
                    help="shard and client on the install alone: the client must build its UltimaLive map copy "
                         "from the install itself (on a UOP-only install, by converting the UOP), and the copy "
                         "is checked block for block against the install")
    ap.add_argument("--windowed", action="store_true",
                    help="editor A and the client in windows (for client.png); they take the desktop's focus. "
                         "Default: headless, and the client's UltimaLive log is the evidence")
    args = ap.parse_args()
    cfg = load_config()
    export = (args.export or cfg.build / "shard_proof_export").resolve()
    out = cfg.build / "editor_live"
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    tools = cfg.tools
    shard_log = cfg.build / "shard_private" / "shard.log"

    # 1. The private shard, with the bridge, on the export.
    sh(str(tools / "editor_shard" / "run.py"), "stop")
    if sh(str(tools / "editor_shard" / "run.py"), "bridge") != 0:
        return 2
    start = ["start"] if args.no_export else ["start", "--data-first", str(export)]
    if sh(str(tools / "editor_shard" / "run.py"), *start) != 0:
        return 2

    # 2. The client's UltimaLive copies for this shard name start fresh.
    ul = Path(os.environ.get("ProgramData", r"C:\ProgramData")) / SHARD_NAME
    shutil.rmtree(ul, ignore_errors=True)

    project = cfg.godot_project
    project_godot = project / "project.godot"
    before = project_godot.read_bytes()
    console = str(cfg.godot_console_exe)
    procs: dict[str, subprocess.Popen] = {}
    try:
        # 3. The client.
        home = out / "client_home"
        (home / "cache").mkdir(parents=True)
        (home / "profiles").mkdir()
        settings = {"profilespath": str(home / "profiles")}
        if not args.no_export:
            settings["files_override"] = str(export / "files_override.txt")
        (home / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
        cmd = [console, *([] if args.windowed else ["--headless"]), "--path", str(project), "--", "--play",
               "--screenshot-dir", str(out), "--screenshot-name", "client"]
        # Always move to the facet under test: the private shard saves where
        # the character was left, so a previous run may have left it elsewhere.
        for c in [f"[self set map {FACET_NAMES[args.facet].lower()}", "[go 1164 1668"] + ["[where"] * 8:
            cmd += ["--shard-command", c]
        env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
               "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(PORT)}
        start_log = shard_log.read_text(encoding="utf-8", errors="replace")
        procs["client"] = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                                           stderr=subprocess.STDOUT, env=env, **no_activate())

        # 4. The two editors.
        def editor(role: str, headless: bool) -> subprocess.Popen:
            c = [console, *(["--headless"] if headless else []), "--editor", "--path", str(project), "--",
                 "--guo-editor-smoke", str(out / role), "--guo-editor-live", role,
                 "--guo-editor-live-port", str(BRIDGE), "--guo-editor-live-as", PROBE_CHARACTER,
                 "--guo-editor-live-facet", str(args.facet)]
            (out / role).mkdir(exist_ok=True)
            e = {**os.environ, "UO_WORLD_PROJECT": str(out / role / "boot_project")}
            return subprocess.Popen(c, stdout=(out / f"editor_{role}.log").open("w", encoding="utf-8", errors="replace"),
                                    stderr=subprocess.STDOUT, env=e, **no_activate())

        procs["follow"] = editor("follow", headless=True)
        if not wait_for(lambda: (out / "follow" / "follow.ready").exists(), 180, "editor B (follow)"):
            return 1
        procs["send"] = editor("send", headless=not args.windowed)
        if not wait_for(lambda: (out / "send" / "send.ready").exists(), 180, "editor A (send)"):
            return 1

        # 5. Go, once the client is on UltimaLive and standing at the block.
        if not wait_for(lambda: "UltimaLive on for" in shard_log.read_text(encoding="utf-8", errors="replace")[len(start_log):],
                        300, "the client on UltimaLive"):
            return 1
        # The shard pushes a block only to clients on that block's map: wait
        # until the client reports standing at the cell on the facet under test.
        here = f"You are at 1164 1668"
        there = f"in {FACET_NAMES[args.facet]}"
        if not wait_for(lambda: any(here in line and there in line for line in
                                    (out / "client.log").read_text(encoding="utf-8", errors="replace").splitlines()),
                        120, f"the client at 1164,1668 {there}"):
            return 1
        time.sleep(4)
        (out / "send" / "go").write_text("", encoding="utf-8")

        for name in ("send", "follow"):
            procs[name].wait(timeout=600)

        # The client: windowed, it photographs its frame and quits. Headless it
        # cannot (its final capture waits for a frame that never renders), so
        # its UltimaLive log line is the evidence: wait for it, then end it.
        if args.windowed:
            procs["client"].wait(timeout=600)
        else:
            wait_for(lambda: "writing statics" in (out / "client.log").read_text(encoding="utf-8", errors="replace"),
                     60, "the client's UltimaLive write")
            procs["client"].kill()
    finally:
        for p in procs.values():
            if p.poll() is None:
                p.kill()
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)

    # 6. Evidence.
    def report(role: str) -> dict:
        f = out / role / "report.json"
        return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {}

    a, b = report("send"), report("follow")
    la, lb = a.get("live", {}), b.get("live", {})
    bridge = [l for l in shard_log.read_text(encoding="utf-8", errors="replace")[len(start_log):].splitlines() if "bridge" in l]
    client_ul = [l for l in (out / "client.log").read_text(encoding="utf-8", errors="replace").splitlines()
                 if "UltimaLive" in l or "writing statics" in l or "NullReference" in l]
    shots = sorted(out.glob("client*.png"))

    def ts(ms):
        return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).strftime("%H:%M:%S.%f")[:-3] if ms else None

    summary = {
        "facet": args.facet,
        "sent_utc": ts(la.get("sent_ms")),
        "round_trip_ms": la.get("round_trip_ms"),
        "pushed_to_clients": la.get("pushed_to_clients"),
        "relayed_to_editors": la.get("relayed_to_editors"),
        "follow_received_utc": ts(lb.get("received_ms")),
        "editor_to_editor_ms": (lb.get("received_ms") - la["sent_ms"]) if lb.get("received_ms") and la.get("sent_ms") else None,
        "follow_has_tree": lb.get("tree_in_this_world"),
        "command_ok": la.get("command_ok"),
        "client_screenshot": str(shots[-1]) if shots else None,
        "bridge_log": bridge,
        "client_ultimalive_log": client_ul,
        "editor_failures": {"send": a.get("failures"), "follow": b.get("failures")},
        "ultimalive_map_copy": map_copy_checks(cfg.client_data, ul) if args.no_export else None,
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    for k, v in summary.items():
        if k not in ("bridge_log", "client_ultimalive_log"):
            print(f"[editor_live] {k}: {v}")
    for line in bridge:
        print(f"[editor_live] shard: {line}")
    for line in client_ul:
        print(f"[editor_live] client: {line}")

    ok = (la.get("stamped") and (la.get("pushed_to_clients") or 0) >= 1 and (la.get("relayed_to_editors") or 0) >= 1
          and lb.get("tree_in_this_world") and summary["editor_to_editor_ms"] is not None
          and summary["editor_to_editor_ms"] < 1000 and la.get("command_ok")
          and not any("NullReference" in l for l in client_ul) and a.get("ok") and b.get("ok")
          and (not args.no_export or summary["ultimalive_map_copy"].get("ok")))
    print("[editor_live] OK" if ok else "[editor_live] FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
