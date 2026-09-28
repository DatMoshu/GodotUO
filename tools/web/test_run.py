"""Tests for tools/web/run.py's export guard, without an engine, for CI. Exit 0/1.

    python tools/web/test_run.py

Checks that an export with no web-capable Godot (the fork) is refused before
it touches anything, that a failed export leaves the previous build in place,
and that a good one replaces it: the engine is a stand-in that writes (or
fails to write) the staged page. Then the data server on an ephemeral port:
it binds 127.0.0.1, serves ranges, answers 416 to an unsatisfiable one, and
never serves a file outside the data folder. Then LAN mode (guo.lan): the
address checks, the local CA and server certificate, and an HTTPS server that
verifies against that CA, serves the CA at /guo-ca.crt, refuses plain http
and turns away a public peer; and the index names no shard host.
"""
from __future__ import annotations

import dataclasses
import http.client
import os
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

import run as web  # noqa: E402

FAILS: list[str] = []

# Example private addresses for the LAN mode checks; no machine's own.
EXAMPLE_LAN = "192.168.1.20"
EXAMPLE_PEER = "192.168.1.30"


def check(ok: bool, what: str) -> None:
    print(f"{'ok  ' if ok else 'FAIL'} {what}")
    if not ok:
        FAILS.append(what)


def paths(tmp: Path, fork: Path | None) -> web.Paths:
    # Everything the export touches lives under root: build/web and the project.
    cfg = dataclasses.replace(web.load_config(), root=tmp, web_godot=fork)
    return web.Paths(cfg)


def previous_build(p: web.Paths) -> None:
    p.out_dir.mkdir(parents=True)
    (p.out_dir / "GUO.html").write_text("old page")
    (p.out_dir / "GUO.wasm").write_text("old wasm")


def fake_engine(writes: bool, code: int):
    """Stands in for web.run: writes the staged page, as the engine would, or not."""
    def fake(cmd, **kw):
        staged = Path(cmd[-1])
        if writes:
            staged.write_text("new page")
            staged.with_suffix(".wasm").write_text("new wasm")
            staged.with_suffix(".js").write_text("".join(anchor for anchor, _ in web.PAGE_PATCHES))
        return subprocess.CompletedProcess(cmd, code)
    return fake


def data_server(tmp: Path) -> None:
    """The /uo/ data server (make_server): bind, Range, 416, the traversal guard."""
    root, data = tmp / "page", tmp / "data"
    root.mkdir()
    data.mkdir()
    (root / "GUO.html").write_text("page")
    (data / "tiledata.mul").write_bytes(bytes(range(100)))
    (tmp / "secret.txt").write_text("outside")
    cfg = dataclasses.replace(web.load_config(), root=tmp, web_port=0)
    server = web.make_server(web.Paths(cfg), root, data)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    host, port = server.server_address[:2]
    check(host == "127.0.0.1", f"data server binds 127.0.0.1 only (got {host})")

    def get(path: str, rng: str | None = None) -> tuple[int, dict, bytes]:
        c = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
        c.request("GET", path, headers={"Range": rng} if rng else {})
        r = c.getresponse()
        body = r.read()
        c.close()
        return r.status, {k.lower(): v for k, v in r.getheaders()}, body

    try:
        status, h, body = get("/uo/tiledata.mul")
        check(status == 200 and body == bytes(range(100)), "a whole file is served")
        status, h, body = get("/uo/tiledata.mul", "bytes=10-19")
        check(status == 206 and body == bytes(range(10, 20)) and h.get("content-range") == "bytes 10-19/100",
              "a range is served as 206 with its Content-Range")
        status, h, body = get("/uo/tiledata.mul", "bytes=90-")
        check(status == 206 and body == bytes(range(90, 100)), "an open-ended range runs to the end")
        for rng in ("bytes=10-5", "bytes=100-", "bytes=abc"):
            status, h, _ = get("/uo/tiledata.mul", rng)
            check(status == 416 and h.get("content-range") == "bytes */100", f"{rng!r} answers 416")
        status, _, body = get("/uo/_index.json")
        check(status == 200 and b"tiledata.mul" in body, "the index lists the data files")
        for bad in ("/uo/../secret.txt", "/uo/%2e%2e/secret.txt", "/uo/nothing.mul"):
            status, _, body = get(bad)
            check(status == 404 and b"outside" not in body, f"{bad} is not served (404)")
    finally:
        server.shutdown()
        server.server_close()


def lan_mode(tmp: Path) -> None:
    """guo.lan and serve --lan, on loopback (the one private address every machine has)."""
    from guo import lan

    for address, private in ((EXAMPLE_LAN, True), ("127.0.0.1", True), ("fe80::1", True), (f"::ffff:{EXAMPLE_LAN}", True),
                             ("8.8.8.8", False), ("::ffff:8.8.8.8", False), ("0.0.0.0", False), ("nonsense", False)):
        check(lan.is_private(address) == private, f"is_private({address}) is {private}")
    for bad in ("8.8.8.8", "127.0.0.1", "0.0.0.0"):
        try:
            lan.lan_address(bad)
            check(False, f"lan_address refuses {bad}")
        except ValueError:
            check(True, f"lan_address refuses {bad}")
    check(lan.lan_address(EXAMPLE_LAN) == EXAMPLE_LAN, "lan_address takes a private override")

    certs = tmp / "certs"
    ca, cert, key = lan.ensure_certificates(certs, "127.0.0.1")
    first = cert.read_bytes(), ca.read_bytes()
    lan.ensure_certificates(certs, "127.0.0.1")
    check((cert.read_bytes(), ca.read_bytes()) == first, "certificates are kept while they still fit")
    lan.ensure_certificates(certs, EXAMPLE_LAN)
    check(ca.read_bytes() == first[1] and cert.read_bytes() != first[0],
          "a new address: a new server certificate, the same CA (installed once)")
    ca, cert, key = lan.ensure_certificates(certs, "127.0.0.1")

    root = tmp / "lanpage"
    root.mkdir()
    (root / "GUO.html").write_text("page")
    cfg = dataclasses.replace(web.load_config(), root=tmp, web_port=0)
    server = web.make_server(web.Paths(cfg), root, None, ("127.0.0.1", ca, lan.server_context(cert, key)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    port = server.server_address[1]
    trust = ssl.create_default_context(cafile=str(ca))
    try:
        c = http.client.HTTPSConnection("127.0.0.1", port, timeout=10, context=trust)
        c.request("GET", "/GUO.html")
        r = c.getresponse()
        check(r.status == 200 and r.read() == b"page", "LAN: the page over https, verified against the local CA")
        check(r.getheader("Cross-Origin-Opener-Policy") == "same-origin", "LAN: still cross-origin isolated")
        c.close()
        c = http.client.HTTPSConnection("localhost", port, timeout=10, context=trust)
        c.request("GET", web.CA_PATH)
        r = c.getresponse()
        check(r.status == 200 and r.read() == ca.read_bytes(), "LAN: the CA at /guo-ca.crt, by name too")
        c.close()
        try:
            c = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            c.request("GET", "/GUO.html")
            c.getresponse().read()
            check(False, "LAN: plain http gets nothing")
        except (OSError, http.client.HTTPException):
            check(True, "LAN: plain http gets nothing")
        check(not server.verify_request(None, ("8.8.8.8", 1)), "LAN: a public peer is turned away")
        check(server.verify_request(None, (EXAMPLE_PEER, 1)), "LAN: a private peer is let in")
    finally:
        server.shutdown()
        server.server_close()

    data = tmp / "landata"
    data.mkdir()
    (data / "tiledata.mul").write_bytes(b"x")
    index = web.json.loads(web.data_index(web.Paths(cfg), data))
    check("host" not in index["shard"], "the index names no shard host (the page uses its own)")

    sys.path.insert(0, str(HERE.parent / "ws_bridge"))
    import importlib.util
    spec = importlib.util.spec_from_file_location("ws_bridge_run", HERE.parent / "ws_bridge" / "run.py")
    bridge = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bridge)
    origins = bridge.lan_origins(EXAMPLE_LAN, 8060)
    check(f"https://{EXAMPLE_LAN}:8060" in origins and f"https://{socket.gethostname().lower()}.local:8060" in origins
          and not any(o.startswith("http:") for o in origins), "bridge: LAN origins are the https page's")


def main() -> int:
    os.environ.pop("GODOT_CONSOLE", None)
    web.ensure_solution = lambda p: None
    web.render_preset = lambda p, export_path: export_path

    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        fork = tmp / "fork_console.exe"
        fork.write_text("")

        # No fork: refused, nothing written or removed.
        p = paths(tmp, None)
        previous_build(p)
        calls = []
        web.run = lambda cmd, **kw: calls.append(cmd)
        check(web.export(p, p.page) == 2, "no fork: export refused")
        check(not calls, "no fork: the engine is never started")
        check((p.out_dir / "GUO.wasm").read_text() == "old wasm", "no fork: the previous build is untouched")
        check(not (p.out_dir / "_export_staging").exists(), "no fork: no staging folder")

    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        fork = tmp / "fork_console.exe"
        fork.write_text("")

        # The engine fails: the previous build stays.
        p = paths(tmp, fork)
        previous_build(p)
        web.run = fake_engine(writes=False, code=1)
        check(web.export(p, p.page) == 1, "failed export: reported as failed")
        check((p.out_dir / "GUO.wasm").read_text() == "old wasm", "failed export: the previous wasm is kept")
        check((p.out_dir / "GUO.html").read_text() == "old page", "failed export: the previous page is kept")

        # The engine succeeds: the new build replaces the old one.
        web.run = fake_engine(writes=True, code=0)
        check(web.export(p, p.page) == 0, "good export: succeeds")
        check((p.out_dir / "GUO.wasm").read_text() == "new wasm", "good export: the new wasm replaces the old")
        check("guoBeforeMain" in (p.out_dir / "GUO.js").read_text(), "good export: the page is patched")
        check((p.out_dir / "guo_data.js").exists(), "good export: guo_data.js is copied next to the page")
        check(not (p.out_dir / "_export_staging").exists(), "good export: the staging folder is gone")

    with tempfile.TemporaryDirectory() as t:
        data_server(Path(t))

    with tempfile.TemporaryDirectory() as t:
        lan_mode(Path(t))

    print("test_run: " + ("FAILED: " + "; ".join(FAILS) if FAILS else "all ok"))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
