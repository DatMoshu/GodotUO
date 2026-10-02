#!/usr/bin/env python3
"""Deploy content packs to a ModernUO shard and publish what its players need (ADR-0026 section 4).

    python tools/shard_content/run.py deploy --name NAME --catalogue URL[=KEY] [--catalogue ...]
                                              --pack ID --version V --bind REF=TYPE:ID [--bind ...]
                                              [--shard-dir DIR] [--scripts allowed|forbidden]
    python tools/shard_content/run.py check   [DESCRIPTOR]
    python tools/shard_content/run.py serve   [--host 127.0.0.1] [--port 18870]
    python tools/shard_content/run.py prove   [--out DIR] [--min-free-gb 16]

deploy installs the pack and what it needs from the named catalogues, exactly
as a player's client will (the same C# installer, through the headless store
tool), writes the deployment lock with the numeric bindings given, and then:

  <shard>/Data/GUO/server-content.json          the neutral export, guo/server-content@1,
                                                which the GUO bridge assembly loads at start
  <shard>/Data/GUO/public/shard-content.json    the descriptor, guo/shard-content@1

A catalogue named without its key is read once and its key is written into the
descriptor; the fingerprint is printed so the operator can compare it with the
one the catalogue publishes. Players approve those keys when they say yes to
the shard's packs, and a catalogue that later answers with another key is
refused.

prove runs the whole path on the editor's private shard: a signed demo catalogue,
deploy, the Servers screen's Play through the pregame probe, and a client logged
in with the packs mounted, making the pack's item (prove.py says how).

The descriptor is public. Serve the public folder from any web server (serve
does it on this computer, for testing) and put its address in the shard's
server list entry as "content". The default shard is the editor's private one
(tools/editor_shard); restart it to load a new export.

Exit codes: 0 ok, 1 failed, 2 bad input or state.
"""

from __future__ import annotations

import argparse
import functools
import json
import os
import re
import shutil
import subprocess
import sys
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from asset_store import ed25519  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "tools/asset_store/headless/StoreSmoke.csproj"
HEADLESS = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"
TYPES = {"static", "land", "texmap", "gump", "hue", "sound", "music", "light", "multi", "tiledata", "animation", "font", "map", "translation"}


def headless(*args: str) -> str:
    r = subprocess.run(["dotnet", str(HEADLESS), *map(str, args)], capture_output=True, text=True, encoding="utf-8")
    if r.returncode:
        raise SystemExit(f"[shard_content] {args[0]} {args[1] if len(args) > 1 else ''} failed:\n{(r.stdout + r.stderr)[-3000:]}")
    return r.stdout


def build_headless() -> None:
    r = subprocess.run(["dotnet", "build", str(PROJECT), "-nologo", "-v", "q"], capture_output=True, text=True)
    if r.returncode:
        raise SystemExit("[shard_content] the headless store tool does not build:\n" + r.stdout[-3000:])


def default_shard(cfg) -> Path:
    return cfg.build / "shard_private"


def parse_binding(text: str) -> tuple[str, dict]:
    m = re.fullmatch(r"([a-z0-9][a-z0-9-]*:[a-z0-9][a-z0-9-]*)=([a-z]+):(\d+)", text.strip())
    if not m or m.group(2) not in TYPES:
        raise SystemExit(f"[shard_content] --bind wants pack:component=type:id, got {text!r}")
    return m.group(1), {"type": m.group(2), "id": int(m.group(3))}


def parse_catalogue(text: str) -> tuple[str, str | None]:
    url, _, key = text.partition("=")
    if key:
        ed25519.decode(key, 32)
    return url.strip(), key.strip() or None


def replace(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(data)
    os.replace(temporary, path)


def cmd_deploy(cfg, a) -> int:
    shard = Path(a.shard_dir) if a.shard_dir else default_shard(cfg)
    if not (shard / "Data").is_dir():
        print(f"[shard_content] {shard} is not a ModernUO install (no Data folder); for the editor's shard run: python tools/editor_shard/run.py setup")
        return 2
    catalogues = [parse_catalogue(c) for c in a.catalogue]
    bindings = dict(parse_binding(b) for b in a.bind)
    if a.bindings:
        bindings.update(json.loads(Path(a.bindings).read_text(encoding="utf-8")))
    build_headless()

    # A fresh store each deploy: what lands in the lock is exactly what the catalogues serve now.
    work = Path(a.work) if a.work else cfg.build / "shard_content" / re.sub(r"[^a-z0-9-]+", "-", a.name.lower()).strip("-")
    if work.exists():
        shutil.rmtree(work)
    store = work / "store"
    store.mkdir(parents=True)
    print(headless("shard-content", "install", store, a.pack, a.version, *[u if k is None else f"{u}={k}" for u, k in catalogues]).strip())

    lock = work / "lock.json"
    headless("content-lock", store, a.pack, a.version, lock)
    value = json.loads(lock.read_text(encoding="utf-8"))
    value["bindings"] = bindings
    lock.write_text(json.dumps(value, indent=2), encoding="utf-8")
    print(headless("shard-content", "lock-check", store, lock).strip())

    export = work / "server-content.json"
    print(headless("export-server", store, lock, export).strip())
    replace(shard / "Data" / "GUO" / "server-content.json", export.read_bytes())

    # The keys the install step approved, for the catalogues named without one.
    listed = store / ".catalogues.json"
    records = json.loads(listed.read_text(encoding="utf-8")) if listed.exists() else []
    trusted = {r["url"].rstrip("/").lower(): r["key"] for r in records if r.get("key")}
    named = []
    for url, key in catalogues:
        found = key or trusted.get(url.rstrip("/").lower())
        named.append({"url": url, "key": found} if found else {"url": url})
        print(f"[shard_content] catalogue {url}: " + (f"key {ed25519.fingerprint(ed25519.decode(found, 32))}" if found else "unsigned (LAN or this computer only)"))

    descriptor = {
        "schema": "guo/shard-content@1",
        "shard": {"name": a.name, **({"host": a.host} if a.host else {}), **({"port": a.port} if a.port else {})},
        "catalogues": named,
        "lock": value,
        "scripts": a.scripts,
    }
    out = Path(a.descriptor) if a.descriptor else shard / "Data" / "GUO" / "public" / "shard-content.json"
    replace(out, (json.dumps(descriptor, indent=2) + "\n").encode("utf-8"))
    print(headless("shard-content", "check", out).strip())
    print(f"[shard_content] deployment {value['identity_hash'][:16]}: export in {shard / 'Data' / 'GUO'}, descriptor {out}")
    print("[shard_content] restart the shard to load it; put the descriptor's address in the server entry's \"content\"")
    return 0


def cmd_check(cfg, a) -> int:
    path = Path(a.descriptor) if a.descriptor else default_shard(cfg) / "Data" / "GUO" / "public" / "shard-content.json"
    build_headless()
    print(headless("shard-content", "check", path).strip())
    return 0


class Quiet(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def cmd_serve(cfg, a) -> int:
    folder = Path(a.dir) if a.dir else default_shard(cfg) / "Data" / "GUO" / "public"
    if not folder.is_dir():
        print(f"[shard_content] nothing to serve: {folder} (run deploy first)")
        return 2
    server = ThreadingHTTPServer((a.host, a.port), functools.partial(Quiet, directory=str(folder)))
    print(f"[shard_content] serving the descriptor at http://{a.host}:{server.server_port}/shard-content.json", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("deploy", help="install, lock and export a shard's packs; write its descriptor")
    d.add_argument("--name", required=True, help="the shard's name, as players see it")
    d.add_argument("--catalogue", action="append", required=True, help="URL or URL=ed25519:KEY; repeat for several")
    d.add_argument("--pack", required=True)
    d.add_argument("--version", required=True)
    d.add_argument("--bind", action="append", default=[], help="pack:component=type:id, the numeric slot a component takes")
    d.add_argument("--bindings", help="a JSON file of bindings, {\"pack:component\": {\"type\": ..., \"id\": ...}}")
    d.add_argument("--scripts", choices=["allowed", "forbidden"], default="allowed")
    d.add_argument("--shard-dir", help="the ModernUO install (default: the editor's private shard)")
    d.add_argument("--host", help="the shard's address, for the descriptor")
    d.add_argument("--port", type=int)
    d.add_argument("--descriptor", help="where to write the descriptor (default <shard>/Data/GUO/public/shard-content.json)")
    d.add_argument("--work", help="working folder (default build/shard_content/<name>)")
    c = sub.add_parser("check", help="parse a descriptor as the client does")
    c.add_argument("descriptor", nargs="?")
    s = sub.add_parser("serve", help="serve the public folder over HTTP, for testing")
    s.add_argument("--dir")
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=18870)
    v = sub.add_parser("prove", help="deploy a demo to the private shard and play it through the client")
    v.add_argument("--out", type=Path)
    v.add_argument("--min-free-gb", type=float, default=16)
    a = p.parse_args()
    cfg = load_config()
    if a.cmd == "prove":
        import prove
        return prove.prove(cfg, a, cmd_deploy)
    return {"deploy": cmd_deploy, "check": cmd_check, "serve": cmd_serve}[a.cmd](cfg, a)


if __name__ == "__main__":
    raise SystemExit(main())
