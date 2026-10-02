"""Prove a shard's content end to end on the private ModernUO (ADR-0026 section 4).

1. A signed catalogue of the starter packs (tools/asset_store/content_examples.py) is made
   under build/shard_content/demo with a throwaway key, and served on this computer.
2. deploy installs sample-content-combined from it (a stone's art and the item that uses it),
   locks it with the art in a static slot, writes the ModernUO export into the private shard and the descriptor beside it,
   which is served too. The shard restarts and its bridge must log the deployment.
3. The pregame probe plays a server entry naming the descriptor through the Servers
   screen: the note, Play, the question, the install, the restart (held back), the
   session. Its own store folder; screenshots of the question and the session.
4. A client starts with that session, as the restart would: the lock mounted, logged in
   to the private shard. "[GUOPackItem" makes the pack's item; the backpack opens; a frame.

Never the shared shard; never with less than --min-free-gb of memory free.
"""
from __future__ import annotations

import argparse
import functools
import json
import os
import shutil
import subprocess
import sys
import threading
import time
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.process import no_activate  # noqa: E402
from asset_store import catalogue, content_examples  # noqa: E402
from asset_store.run import make_index  # noqa: E402

PACK, VERSION, ITEM = "sample-content-combined", "1.0.0", "sample-content-combined:stone-item"
SLOT = 6001   # the static the pack's stone art takes in this deployment


class Quiet(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def serve(folder: Path) -> tuple[ThreadingHTTPServer, str]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(folder)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_port}/"


def free_gb() -> float:
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB"],
                         capture_output=True, text=True).stdout.strip()
    return float(out or 0)


def wait_for(pred, timeout: float) -> bool:
    end = time.time() + timeout
    while time.time() < end:
        if pred():
            return True
        time.sleep(0.5)
    return pred()


def demo_catalogue(cfg) -> Path:
    """The starter packs in a store folder, signed with a key made for this proof only."""
    demo = cfg.build / "shard_content" / "demo"
    key = demo / "catalogue.key"
    if not key.exists():
        demo.mkdir(parents=True, exist_ok=True)
        catalogue.keygen(key)
    cdn = demo / "cdn"
    if not (cdn / "packs" / PACK).is_dir():
        content_examples.build(demo / "examples", cdn)
    make_index(cdn, (catalogue.read_secret(key), {"id": "guo-content-demo", "title": "GUO content demo"}, ""))
    return cdn


def prove(cfg, a, deploy) -> int:
    gb = free_gb()
    if gb < a.min_free_gb:
        print(f"[prove] only {gb:.1f} GB free; a shard and two clients need {a.min_free_gb:.0f} GB free. Not started.")
        return 3
    shard_home = cfg.build / "shard_private"
    state_file = shard_home / "state.json"
    if not state_file.exists():
        print("[prove] no private shard; run: python tools/editor_shard/run.py setup --port N, then bridge")
        return 2
    state = json.loads(state_file.read_text(encoding="utf-8"))
    port = state["port"]
    if port in (2593, 2594):
        print(f"[prove] the private shard here listens on {port}, a shared port; run editor_shard setup --port N")
        return 2
    out = (a.out or cfg.build / "shard_content" / f"prove-{time.strftime('%Y%m%d-%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    shard = [sys.executable, str(cfg.tools / "editor_shard" / "run.py")]
    report: dict = {"shard_port": port}

    cdn = demo_catalogue(cfg)
    packs, packs_url = serve(cdn)
    published = shard_home / "Data" / "GUO" / "public"
    try:
        if deploy(cfg, argparse.Namespace(
                name="GUO content demo", catalogue=[packs_url], pack=PACK, version=VERSION,
                bind=[f"sample-content-combined:stone=static:{SLOT}"], bindings=None, scripts="forbidden",
                shard_dir=str(shard_home), host="127.0.0.1", port=port, descriptor=None,
                work=str(out / "deploy"))) != 0:
            return 1
        descriptor_server, descriptor_base = serve(published)
        descriptor_url = descriptor_base + "shard-content.json"
        identity = json.loads((published / "shard-content.json").read_text(encoding="utf-8"))["lock"]["identity_hash"]
        report["identity"] = identity

        subprocess.run([*shard, "stop"])
        if subprocess.run([*shard, "start"]).returncode != 0:
            return 2
        log = (shard_home / "shard.log").read_text(encoding="utf-8", errors="replace")
        report["shard_loaded"] = f"Deployment {identity}" in log
        print(f"[prove] shard loaded the deployment: {report['shard_loaded']}", flush=True)

        common = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CLIENT_VERSION": cfg.client_version,
                  "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(port)}
        common.pop("UO_CONTENT_LOCK", None)

        # 3. The Servers screen: Play on the content shard, through the pregame probe.
        pregame = out / "pregame_home"
        (pregame / "cache").mkdir(parents=True, exist_ok=True)
        session_copy = out / "shard_session.json"
        env = {**common, "UO_CACHE_DIR": str(pregame / "cache"), "UO_CONTENT_STORE": str(out / "store"),
               "UO_PROBE_SHARD_CONTENT": descriptor_url, "UO_PROBE_SHARD_CONTENT_SESSION": str(session_copy)}
        cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--pregame-probe",
               "--window-size", "1280,800", "--cache-dir", str(pregame / "cache"),
               "--screenshot-dir", str(out), "--screenshot-name", "pregame"]
        with (out / "pregame.log").open("w", encoding="utf-8", errors="replace") as f:
            r = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, env=env, timeout=900, **no_activate())
        text = (out / "pregame.log").read_text(encoding="utf-8", errors="replace")
        report["pregame"] = [ln.strip() for ln in text.splitlines() if "pregame probe:" in ln or "content" in ln.lower() and "[GUO]" in ln][-12:]
        report["pregame_exit"] = r.returncode
        if not session_copy.exists():
            print("[prove] the pregame probe wrote no session; see", out / "pregame.log")
            return 1

        # 4. The restart: a client with the session the probe wrote, logged in to the private shard.
        home = out / "client_home"
        (home / "cache").mkdir(parents=True, exist_ok=True)
        (home / "profiles").mkdir(exist_ok=True)
        session = json.loads(session_copy.read_text(encoding="utf-8"))
        session["port"] = port   # the probe's entry stands for this shard
        (home / "shard_session.json").write_text(json.dumps(session, indent=2), encoding="utf-8")
        (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
        (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
        watch = out / "watch"
        shutil.rmtree(watch, ignore_errors=True)
        watch.mkdir()
        env = {**common, "UO_CACHE_DIR": str(home / "cache"), "UO_CONTENT_STORE": str(out / "store")}
        cmd = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--window-size", "1024,768",
               "--cache-dir", str(home / "cache"), "--screenshot-dir", str(out), "--screenshot-name", "end",
               "--objects-watch", str(watch), "--shard-command", "[self set map felucca", "--shard-command", "[where"]
        client = subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                                  stderr=subprocess.STDOUT, env=env, **no_activate())
        try:
            if not wait_for(lambda: (watch / "watching").exists(), 300):
                print("[prove] the client never reached its watch")
                return 1
            time.sleep(4)
            (watch / "login.closegumps").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / "login.closed").exists(), 30)
            (watch / "item.say").write_text(f"[GUOPackItem {ITEM}", encoding="utf-8")
            wait_for(lambda: (watch / "item.said").exists(), 45)
            time.sleep(3)
            (watch / "pack.backpack").write_text("", encoding="utf-8")
            time.sleep(3)
            (watch / "item.request").write_text("", encoding="utf-8")
            (watch / "item.shot").write_text("", encoding="utf-8")
            wait_for(lambda: (watch / "item.json").exists() and (watch / "item.png").exists(), 60)
            time.sleep(0.3)
            if (watch / "item.png").exists():
                shutil.copy(watch / "item.png", out / "in_game_item.png")
            dump = json.loads((watch / "item.json").read_text(encoding="utf-8")) if (watch / "item.json").exists() else {}
            (watch / "quit").write_text("", encoding="utf-8")
            wait_for(lambda: client.poll() is not None, 30)
        finally:
            if client.poll() is None:
                client.kill()
        client_log = (out / "client.log").read_text(encoding="utf-8", errors="replace")
        report["session_used"] = [ln.strip() for ln in client_log.splitlines() if "shard session" in ln or "content" in ln.lower() and "[GUO" in ln][:12]
        # the shard made it from the export, and the client knows it by the slot the lock gave the art
        report["backpack"] = [i for i in dump.get("backpack", []) if i.get("graphic") == f"0x{SLOT:04X}"]
        report["item_in_dump"] = bool(report["backpack"])
    finally:
        packs.shutdown()
        try:
            descriptor_server.shutdown()
        except NameError:
            pass
        subprocess.run([*shard, "stop"])

    (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    print(f"[prove] frames and logs in {out}")
    ok = report.get("shard_loaded") and report.get("pregame_exit") == 0 and report.get("item_in_dump")
    return 0 if ok else 1
