"""Tests for tools/web/run.py's export guard, without an engine, for CI. Exit 0/1.

    python tools/web/test_run.py

Checks that an export with no web-capable Godot (the fork) is refused before
it touches anything, that a failed export leaves the previous build in place,
and that a good one replaces it: the engine is a stand-in that writes (or
fails to write) the staged page.
"""
from __future__ import annotations

import dataclasses
import os
import subprocess
import sys
import tempfile
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

    print("test_run: " + ("FAILED: " + "; ".join(FAILS) if FAILS else "all ok"))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
