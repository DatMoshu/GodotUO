"""Tests for tools/web/run.py's export guard, without an engine, for CI. Exit 0/1.

    python tools/web/test_run.py

Checks that an export with no web-capable Godot (the fork) is refused before
it touches anything, that a failed export leaves the previous build in place,
and that a good one replaces it: the engine is a stand-in that writes (or
fails to write) the staged page. Then the data server on an ephemeral port:
it binds 127.0.0.1, serves ranges, answers 416 to an unsatisfiable one, and
never serves a file outside the data folder.
"""
from __future__ import annotations

import dataclasses
import http.client
import os
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

    print("test_run: " + ("FAILED: " + "; ".join(FAILS) if FAILS else "all ok"))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
