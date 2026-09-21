#!/usr/bin/env python3
"""Port verbatim- and shim-tier files from ClassicUO into the Godot project.

Four fifths of this port is mechanical: change the namespace, change the
`using` lines, move the file. Doing that by hand for 345 files would be slow
and, worse, inconsistent -- and inconsistency is what makes a future upstream
merge painful. So it is done here, identically, every time.

What this tool deliberately does NOT do:

  * touch rewrite-tier files (the renderer, input, audio). Those need
    judgement, not translation.
  * reformat, rename, modernise, or fix bugs. A faithful port stays
    mergeable with live upstream; an "improved" one does not.

Usage:
    python tools/port_bulk/run.py --area Utility [--area IO] [--dry-run]
    python tools/port_bulk/run.py --all-mechanical
    python tools/port_bulk/run.py --area Game --tier verbatim

Through the launcher:
    launchers\\pipeline\\04_port_bulk.bat --area Utility
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

# Reuse the audit's classifier so "what tier is this?" has exactly one answer.
# Loaded by path, not by name: both tools have a module called `run`, and a
# plain import would collide with this one.
import importlib.util as _ilu  # noqa: E402

_audit_path = Path(__file__).resolve().parents[1] / "port_audit" / "run.py"
_spec = _ilu.spec_from_file_location("guo_port_audit", _audit_path)
_audit = _ilu.module_from_spec(_spec)
_spec.loader.exec_module(_audit)  # type: ignore[union-attr]

AREA_MAP = _audit.AREA_MAP
area_for = _audit.area_for
classify = _audit.classify

# Areas Godot replaces outright. Upstream's Bootstrap is FNA's game-loop and
# native plugin host; Godot is the host now, and `src/Bootstrap/Main.cs` is our
# own entry point. Porting these produced a nested src/Bootstrap/src/ that only
# dragged in the cuoapi plugin surface, so they are skipped at the source.
REPLACED_BY_ENGINE = set(_audit.REPLACED_BY_ENGINE)

TIER_VERBATIM = "verbatim"
TIER_SHIM = "shim"
MECHANICAL = (TIER_VERBATIM, TIER_SHIM)


# --- C#-aware identifier rewriting ------------------------------------------
#
# A blind textual replace of "ClassicUO" would also rewrite string literals:
# the settings filename, the window title, log prefixes, the user agent, and
# several URLs. Those are behaviour, not namespaces, and changing them would
# be a real (and very hard to spot) bug. So the rewrite skips anything inside
# a string, a char literal, or a comment.


def _split_code_and_text(src: str) -> list[tuple[str, bool]]:
    """Split C# source into (chunk, is_code) runs.

    is_code=False covers string literals, char literals and comments -- the
    regions where identifiers must be left exactly as the author wrote them.
    Interpolated strings are treated as text wholesale: a `{...}` hole could
    contain a qualified name, but ClassicUO has none that need rewriting, and
    mangling a format string is far worse than missing one.
    """
    out: list[tuple[str, bool]] = []
    i, n = 0, len(src)
    start = 0

    def flush(to: int, is_code: bool) -> None:
        if to > start:
            out.append((src[start:to], is_code))

    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ""

        # line comment
        if c == "/" and nxt == "/":
            flush(i, True)
            j = src.find("\n", i)
            j = n if j == -1 else j
            out.append((src[i:j], False))
            i = start = j
            continue

        # block comment
        if c == "/" and nxt == "*":
            flush(i, True)
            j = src.find("*/", i + 2)
            j = n if j == -1 else j + 2
            out.append((src[i:j], False))
            i = start = j
            continue

        # verbatim / interpolated-verbatim string: @"..." or $@"..." or @$"..."
        if c in "@$" and _is_verbatim_start(src, i):
            flush(i, True)
            j = _scan_verbatim(src, i)
            out.append((src[i:j], False))
            i = start = j
            continue

        # regular or interpolated string: "..." or $"..."
        if c == '"' or (c == "$" and nxt == '"'):
            flush(i, True)
            j = _scan_regular(src, i + 1 if c == "$" else i)
            out.append((src[i:j], False))
            i = start = j
            continue

        # char literal
        if c == "'":
            flush(i, True)
            j = _scan_regular(src, i, quote="'")
            out.append((src[i:j], False))
            i = start = j
            continue

        i += 1

    flush(n, True)
    return out


def _is_verbatim_start(src: str, i: int) -> bool:
    if src[i] == "@":
        return src[i + 1 : i + 2] == '"' or src[i + 1 : i + 3] == '$"'
    if src[i] == "$":
        return src[i + 1 : i + 3] == '@"'
    return False


def _scan_verbatim(src: str, i: int) -> int:
    """Index just past a @"..." literal, where "" is an escaped quote."""
    j = src.index('"', i) + 1
    n = len(src)
    while j < n:
        if src[j] == '"':
            if j + 1 < n and src[j + 1] == '"':
                j += 2
                continue
            return j + 1
        j += 1
    return n


def _scan_regular(src: str, i: int, quote: str = '"') -> int:
    """Index just past a "..." or '...' literal, honouring backslash escapes."""
    j = i + 1
    n = len(src)
    while j < n:
        if src[j] == "\\":
            j += 2
            continue
        if src[j] == quote:
            return j + 1
        if src[j] == "\n":  # unterminated; bail rather than eat the file
            return j
        j += 1
    return n


NS_TOKEN = re.compile(r"\bClassicUO\b")


def rewrite_source(text: str, tier: str) -> tuple[str, list[str]]:
    """Apply the port's namespace rules to one file. Returns (text, notes)."""
    notes: list[str] = []

    chunks = _split_code_and_text(text)
    rebuilt = []
    hits = 0
    for chunk, is_code in chunks:
        if is_code:
            chunk, n = NS_TOKEN.subn("GUO", chunk)
            hits += n
        rebuilt.append(chunk)
    out = "".join(rebuilt)
    if hits:
        notes.append(f"namespace:{hits}")

    if tier == TIER_SHIM:
        # The whole point of GUO.Compat: these files import XNA only for the
        # math and colour value types, which Compat provides.
        # ﻿: several upstream files start with a UTF-8 BOM, which sits
        # between the line start and `using` and is not whitespace, so the
        # anchor misses without it.
        out, n = re.subn(
            r"^(\s*﻿?\s*)using\s+Microsoft\.Xna\.Framework\s*;\s*$",
            r"\1using GUO.Compat;",
            out,
            flags=re.MULTILINE,
        )
        if n:
            notes.append(f"compat:{n}")
        left = re.findall(r"using\s+Microsoft\.Xna\.[\w.]+\s*;", out)
        if left:
            notes.append("XNA-LEFT:" + ",".join(sorted(set(left))))

    # SDL comes in with FNA. Nearly all of it is the two key enums, which
    # GUO.Platform.Sdl provides -- see that file's header for the measurement.
    out, n = re.subn(
        r"^(\s*﻿?\s*)using\s+SDL3\s*;\s*$",
        r"\1using GUO.Platform.Sdl;",
        out,
        flags=re.MULTILINE,
    )
    if n:
        notes.append(f"sdl:{n}")

    return out, notes


# --- placement --------------------------------------------------------------


def dest_for(rel: Path, area: str) -> Path:
    """Where an upstream file lands in the port.

    The upstream project prefix (e.g. `ClassicUO.Client/Game/`) is stripped
    and replaced by the area, so `ClassicUO.Client/Game/Scenes/GameScene.cs`
    becomes `src/Game/Scenes/GameScene.cs`. Structure below that is kept, so
    a reviewer can still line the two trees up side by side.
    """
    key = rel.as_posix()
    prefix = max(
        (p for p in AREA_MAP if key.startswith(p)), key=len, default=""
    )
    tail = key[len(prefix) :].lstrip("/")
    return Path(area) / tail


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="port_bulk")
    ap.add_argument("--area", action="append", default=[], help="area to port")
    ap.add_argument(
        "--tier", action="append", default=[], choices=list(MECHANICAL)
    )
    ap.add_argument(
        "--all-mechanical",
        action="store_true",
        help="every verbatim and shim file, all areas",
    )
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument(
        "--force", action="store_true", help="re-port files already present"
    )
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    if not args.area and not args.all_mechanical:
        ap.error("give --area, or --all-mechanical")

    cfg = load_config()
    upstream_src = cfg.upstream / "src"
    port_src = cfg.godot_project / "src"
    if not upstream_src.is_dir():
        print(f"[bulk] FATAL: upstream not found: {upstream_src}")
        return 2

    tiers = tuple(args.tier) if args.tier else MECHANICAL
    areas = set(args.area)

    written = skipped = unchanged = 0
    flagged: list[str] = []

    for path in sorted(upstream_src.rglob("*.cs")):
        if {"obj", "bin"} & set(path.parts):
            continue
        rel = path.relative_to(upstream_src)
        area = area_for(rel)
        if area in REPLACED_BY_ENGINE:
            continue
        if areas and area not in areas:
            continue

        text = path.read_text(encoding="utf-8", errors="replace")
        tier = classify(text, area, rel)
        if tier not in tiers:
            continue

        dest = port_src / dest_for(rel, area)
        if dest.exists() and not args.force:
            unchanged += 1
            continue

        ported, notes = rewrite_source(text, tier)
        bad = [n for n in notes if n.startswith("XNA-LEFT")]
        if bad:
            flagged.append(f"{rel.as_posix()}  {bad[0]}")

        if args.dry_run:
            skipped += 1
            if not args.quiet:
                print(f"  would write {dest.relative_to(port_src)}")
            continue

        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(ported, encoding="utf-8", newline="\n")
        written += 1

    print(
        f"[bulk] wrote {written}, already present {unchanged}"
        + (f", dry-run {skipped}" if args.dry_run else "")
    )
    if flagged:
        print(f"[bulk] !! {len(flagged)} file(s) still reference XNA subsystems.")
        print("[bulk]    These are misclassified or need Compat types:")
        for line in flagged[:20]:
            print(f"      {line}")
        if len(flagged) > 20:
            print(f"      ... and {len(flagged) - 20} more")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
