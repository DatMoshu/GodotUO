#!/usr/bin/env python3
"""Measure how far each ported file has drifted from its upstream original.

`tools/port_audit` scores *whether* a file was ported; this measures *how
much it changed on the way*. Rule 2 ("port faithfully... every gratuitous
edit must be reconciled by hand on every future upstream merge") is only
followed if drift is measured, not remembered; this tool makes that
mechanical.

For every `.cs` under `godot/GUO/src`, the upstream file with the same
filename and the longest common path suffix is picked as its match (if any).
Both files are normalised the same way the porting rules describe --
CRLF -> LF, BOM stripped, `ClassicUO` -> `GUO`, the shim `using` lines
dropped, trailing whitespace stripped -- and diffed. Files whose only
difference is blank lines are treated as identical: rule 2 is about
behaviour, not formatting.

Usage:
    python tools/port_drift/run.py [--strict] [--verbose]

Or through the launcher:
    launchers\\dev\\port_drift.bat
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

# Lines a file carries only because of the shim transform (or because it is a
# GUO-only file), dropped before comparison so a mechanically-ported file
# reads identical to its upstream original.
_DROP_LINE_RE = re.compile(
    r"^using\s+(GUO\.Compat|GUO\.Platform\.Sdl|SDL3|Microsoft\.Xna\b[\w.]*)\s*;\s*$"
)

MARKER_RE = re.compile(r"PORT DEVIATION|PORT GAP")

# Files port_plan.md section 2 lists as carrying the vector-helper spelling
# (XNA's static Vector3 helpers are Godot instance calls); no marker needed.
VECTOR_SPELLING_FILES = {
    "Game/GameObjects/Land.cs",
    "Game/GameObjects/MovingEffect.cs",
    "Game/Weather.cs",
}

# Not a port: Godot replaces upstream's Bootstrap outright (port_status.md).
SKIP_PREFIXES = ("Bootstrap/",)


def normalize(text: str) -> list[str]:
    """Return the comparable lines of a file: CRLF/BOM/renamespace/shim-import
    differences removed, each line right-stripped."""
    text = text.lstrip("﻿").replace("\r\n", "\n").replace("\r", "\n")
    text = text.replace("ClassicUO", "GUO")
    lines: list[str] = []
    for line in text.split("\n"):
        if _DROP_LINE_RE.match(line.strip()):
            continue
        # The shim tier's spellings, port_plan.md section 2: XNA's
        # fully-qualified value types are Compat's, SDL3's static import is
        # Platform.Sdl's. Applied after the drop so `using` lines stay droppable.
        line = line.replace("Microsoft.Xna.Framework.Graphics.Blend", "GUO.Renderer.Blend")
        line = line.replace("Microsoft.Xna.Framework.", "GUO.Compat.")
        line = line.replace("using static SDL3.SDL;", "using static GUO.Platform.Sdl.SDL;")
        lines.append(line.rstrip())
    return lines


def non_blank(lines: list[str]) -> list[str]:
    return [l for l in lines if l.strip() != ""]


def find_upstream_candidates(name: str, upstream_src: Path) -> list[Path]:
    return sorted(upstream_src.rglob(name))


def common_suffix_len(a: tuple[str, ...], b: tuple[str, ...]) -> int:
    n = 0
    for x, y in zip(reversed(a), reversed(b)):
        if x != y:
            break
        n += 1
    return n


def best_match(port_rel: Path, candidates: list[Path], upstream_src: Path) -> Path | None:
    if not candidates:
        return None
    if len(candidates) == 1:
        return candidates[0]
    scored = [
        (common_suffix_len(port_rel.parts, c.relative_to(upstream_src).parts), c)
        for c in candidates
    ]
    scored.sort(key=lambda t: (-t[0], str(t[1])))
    return scored[0][1]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--strict", action="store_true", help="exit 1 if any unmarked change exists")
    ap.add_argument("--verbose", action="store_true", help="list every changed file, not just unmarked ones")
    args = ap.parse_args(argv)

    cfg = load_config()
    upstream_src = cfg.upstream / "src"
    port_src = cfg.godot_project / "src"

    if not upstream_src.is_dir():
        print(f"[port_drift] FATAL: upstream source not found: {upstream_src}", file=sys.stderr)
        print("[port_drift] Run: launchers\\dev\\sync_upstream.bat", file=sys.stderr)
        return 2
    if not port_src.is_dir():
        print(f"[port_drift] FATAL: port source not found: {port_src}", file=sys.stderr)
        return 2

    identical: list[Path] = []
    marked: list[Path] = []
    unmarked: list[Path] = []
    new: list[Path] = []

    for port_file in sorted(port_src.rglob("*.cs")):
        parts = set(port_file.parts)
        if {"obj", "bin", ".godot"} & parts:
            continue
        port_rel = port_file.relative_to(port_src)
        rel_posix = port_rel.as_posix()
        if rel_posix.startswith(SKIP_PREFIXES):
            continue

        candidates = find_upstream_candidates(port_file.name, upstream_src)
        match = best_match(port_rel, candidates, upstream_src)
        if match is None:
            new.append(port_rel)
            continue

        try:
            port_text = port_file.read_text(encoding="utf-8", errors="replace")
            up_text = match.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            print(f"[port_drift] WARNING: could not read {port_file} or {match}: {exc}", file=sys.stderr)
            continue

        port_lines = normalize(port_text)
        up_lines = normalize(up_text)

        if rel_posix in VECTOR_SPELLING_FILES:
            marked.append(port_rel)
            continue

        if port_lines == up_lines or non_blank(port_lines) == non_blank(up_lines):
            identical.append(port_rel)
            continue

        if MARKER_RE.search(port_text):
            marked.append(port_rel)
        else:
            unmarked.append(port_rel)

    print(f"[port_drift] identical after normalisation: {len(identical)}")
    print(f"[port_drift] changed, marked (PORT DEVIATION / PORT GAP): {len(marked)}")
    print(f"[port_drift] changed, UNMARKED: {len(unmarked)}")
    for rel in unmarked:
        print(f"[port_drift]   unmarked: {rel.as_posix()}")
    print(f"[port_drift] new files (no upstream counterpart by filename): {len(new)}")
    if args.verbose:
        for rel in marked:
            print(f"[port_drift]   marked: {rel.as_posix()}")
        for rel in new:
            print(f"[port_drift]   new: {rel.as_posix()}")

    if args.strict and unmarked:
        print(f"[port_drift] FAILED: {len(unmarked)} unmarked change(s) under --strict", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
