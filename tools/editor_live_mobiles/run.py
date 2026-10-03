#!/usr/bin/env python3
"""The Live map layer's feed, proved on the private shard (ADR-0027).

    python tools/editor_live_mobiles/run.py [--shard-port 2615] [--bridge-port 2616]

1. Starts the private shard (tools/editor_shard, never the dev shard 2593) with
   the bridge, if it is not already up.
2. Raw op: logs a client in at 1164,1668 (a probe player), then speaks the
   bridge's "mobiles" op over TCP: the player must come back with serial,
   name, body, position, hits and notoriety; a rate-limited second request, a
   non-staff "as", and an oversized rectangle are checked too.
3. Editor: a headless editor (--guo-editor-live mobiles) turns the Live layer
   on; the Shard dock polls the op and the layer must hold the player.

Prints real output lines; exit 0 when every check passed, 1 otherwise, 2 when
a step could not run. Writes build/editor_live_mobiles/report.json.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402


def wait_for(pred, timeout: float) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


class Bridge:
    def __init__(self, port: int):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.f = self.s.makefile("rw", encoding="utf-8", newline="\n")

    def ask(self, msg: dict, want: str = "mobiles") -> dict:
        self.f.write(json.dumps(msg) + "\n")
        self.f.flush()
        while True:
            line = json.loads(self.f.readline())
            if line.get("op") == want:
                return line


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--shard-port", type=int, default=2615)
    ap.add_argument("--bridge-port", type=int, default=2616)
    args = ap.parse_args()
    cfg = load_config()
    out = cfg.build / "editor_live_mobiles"
    out.mkdir(parents=True, exist_ok=True)
    es = [sys.executable, str(cfg.tools / "editor_shard" / "run.py")]
    if subprocess.run(es + ["status"], capture_output=True, text=True).stdout.find("running") < 0:
        if subprocess.run(es + ["start"]).returncode != 0:
            return 2

    # The client: a probe player standing at 1164,1668.
    home = out / "client_home"
    (home / "cache").mkdir(parents=True, exist_ok=True)
    (home / "profiles").mkdir(exist_ok=True)
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    (home / "profiles" / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")
    watch = out / "watch"
    watch.mkdir(exist_ok=True)
    for f in watch.iterdir():
        f.unlink()
    cmd = [str(cfg.godot_console_exe), "--headless", "--path", str(cfg.godot_project), "--", "--play",
           "--objects-watch", str(watch),
           "--shard-command", "[self set map felucca", "--shard-command", "[go 1164 1668", "--shard-command", "[where"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(args.shard_port)}
    procs = []
    checks: dict[str, bool] = {}
    report: dict = {}
    try:
        procs.append(subprocess.Popen(cmd, stdout=(out / "client.log").open("w", encoding="utf-8", errors="replace"),
                                      stderr=subprocess.STDOUT, env=env, **no_activate()))
        if not wait_for(lambda: (watch / "watching").exists(), 240):
            print("[live_mobiles] the client never reached its watch")
            return 2
        time.sleep(3)

        b = Bridge(args.bridge_port)
        b.ask({"op": "hello", "editor": "live-mobiles-proof"}, "hello")
        box = {"op": "mobiles", "req": 1, "facet": 0, "x0": 1100, "y0": 1600, "x1": 1230, "y1": 1740}
        r = b.ask(box)
        print(f"[live_mobiles] reply: count={r.get('count')} truncated={r.get('truncated')} ok={r.get('ok')}")
        for m in r.get("mobiles", [])[:5]:
            print(f"[live_mobiles]   {json.dumps(m)}")
        players = [m for m in r.get("mobiles", []) if m["isPlayer"]]
        near = [m for m in players if abs(m["x"] - 1164) <= 4 and abs(m["y"] - 1668) <= 4]
        checks["op_returns_player_at_spot"] = bool(near)
        checks["fields_present"] = bool(near) and all(k in near[0] for k in (
            "serial", "name", "body", "x", "y", "z", "facet", "isPlayer", "hits", "maxHits", "notoriety"))
        report["raw"] = r

        time.sleep(0.4)
        far = b.ask({**box, "req": 2, "x0": 5000, "x1": 5100, "y0": 100, "y1": 200})
        print(f"[live_mobiles] far rectangle: ok={far.get('ok')} count={far.get('count')} error={far.get('error')}")
        checks["other_rectangle_excludes_player"] = bool(far.get("ok")) and not any(m["serial"] == near[0]["serial"] for m in far.get("mobiles", [])) if near else False
        time.sleep(0.4)
        r1 = b.ask({**box, "req": 3})
        r2 = b.ask({**box, "req": 4})
        print(f"[live_mobiles] back to back: first ok={r1.get('ok')}, second ok={r2.get('ok')} error={r2.get('error')}")
        checks["rate_limited"] = r2.get("ok") is False and r2.get("error") == "rate limited"
        time.sleep(0.4)
        ns = b.ask({**box, "req": 5, "as": "nobody-online-by-this-name"})
        print(f"[live_mobiles] unknown 'as': ok={ns.get('ok')} error={ns.get('error')}")
        checks["unknown_as_refused"] = ns.get("ok") is False
        time.sleep(0.4)
        big = b.ask({"op": "mobiles", "req": 6, "facet": 0, "x0": 0, "y0": 0, "x1": 6000, "y1": 5000})
        span_ok = big.get("ok") and big["x1"] - big["x0"] <= 1024 and big["y1"] - big["y0"] <= 1024
        print(f"[live_mobiles] oversized rectangle clamped to {big.get('x0')},{big.get('y0')}..{big.get('x1')},{big.get('y1')}")
        checks["span_capped"] = bool(span_ok)
        time.sleep(0.4)
        nomap = b.ask({"op": "mobiles", "req": 7, "facet": 99, "x0": 0, "y0": 0, "x1": 5, "y1": 5})
        checks["bad_facet_refused"] = nomap.get("ok") is False

        # The editor.
        ed = out / "editor"
        ed.mkdir(exist_ok=True)
        ecmd = [str(cfg.godot_console_exe), "--headless", "--editor", "--path", str(cfg.godot_project), "--",
                "--guo-editor-smoke", str(ed), "--guo-editor-live", "mobiles", "--guo-editor-live-port", str(args.bridge_port)]
        eenv = {**os.environ, "UO_WORLD_PROJECT": str(ed / "boot_project")}
        ep = subprocess.Popen(ecmd, stdout=(out / "editor.log").open("w", encoding="utf-8", errors="replace"),
                              stderr=subprocess.STDOUT, env=eenv, **no_activate())
        procs.append(ep)
        try:
            ep.wait(timeout=400)
        except subprocess.TimeoutExpired:
            ep.kill()
        rep = ed / "report.json"
        live = json.loads(rep.read_text(encoding="utf-8")).get("live", {}) if rep.exists() else {}
        report["editor"] = live
        for k in ("mobiles_polls", "mobiles_reply_count", "mobiles_layer_items", "mobiles_player_serial"):
            print(f"[live_mobiles] editor {k}: {live.get(k)}")
        checks["editor_layer_holds_player"] = bool(live.get("ok")) and live.get("mobiles_layer_items", 0) >= 1
        (watch / "quit").write_text("", encoding="utf-8")
    finally:
        for p in procs:
            if p.poll() is None:
                time.sleep(2)
                p.kill()

    log = cfg.build / "shard_private" / "shard.log"
    report["checks"] = checks
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for k, v in checks.items():
        print(f"[live_mobiles]   {'ok  ' if v else 'FAIL'} {k}")
    ok = all(checks.values())
    print("[live_mobiles] OK" if ok else f"[live_mobiles] FAILED (shard log: {log})")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
