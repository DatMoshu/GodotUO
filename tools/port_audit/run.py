#!/usr/bin/env python3
"""Score how much of ClassicUO has been ported into the Godot project.

The audit exists because "port an MMO client" is otherwise an unmeasurable
task. It walks every C# file upstream, classifies how hard that file is to
port, works out where it belongs in the Godot project, and reports what is
done against what is left.

Classification (see docs/port_plan.md for the reasoning):

    verbatim  No XNA/FNA reference at all. Copy across, fix the namespace,
              done. These are pure logic: parsers, math, collections.

    shim      Imports only `Microsoft.Xna.Framework` -- the math and colour
              structs (Point, Color, Rectangle, Vector2/3, Matrix). Once
              GUO.Compat provides XNA-compatible versions of those backed
              by Godot types, these files compile essentially unchanged.

    rewrite   Touches Graphics, Input, Audio or Media, or lives in the
              renderer. These are the files that genuinely bind to FNA and
              must be reimplemented against Godot.

Usage:
    python tools/port_audit/run.py [--out docs/port_status.md] [--json FILE]

Or through the launcher, which resolves paths the same way the game does:
    launchers\\pipeline\\03_port_audit.bat
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

# --- classification ---------------------------------------------------------

HEAVY_NS = re.compile(
    r"using\s+Microsoft\.Xna\.Framework\.(Graphics|Input|Audio|Media|Content)"
)
ANY_XNA = re.compile(r"Microsoft\.Xna")

TIER_VERBATIM = "verbatim"
TIER_SHIM = "shim"
TIER_REWRITE = "rewrite"

TIER_ORDER = (TIER_VERBATIM, TIER_SHIM, TIER_REWRITE)

# Upstream project (or project/subfolder) -> destination area in the port.
# Longest prefix wins, so more specific rules may be listed in any order.
AREA_MAP: dict[str, str] = {
    "ClassicUO.Utility": "Utility",
    "ClassicUO.IO/Audio": "Audio",
    "ClassicUO.IO": "IO",
    "ClassicUO.Assets": "Assets",
    "ClassicUO.Renderer": "Render",
    "ClassicUO.Bootstrap": "Bootstrap",
    "ClassicUO.Client/Network": "Network",
    "ClassicUO.Client/Game/UI": "Game/UI",
    "ClassicUO.Client/Game": "Game",
    "ClassicUO.Client/Input": "Input",
    "ClassicUO.Client/Configuration": "Configuration",
    "ClassicUO.Client/Resources": "Resources",
    "ClassicUO.Client": "Client",
}

# Areas that are replaced by Godot rather than ported. Files here are counted
# separately so they do not distort the "remaining work" figure.
REPLACED_BY_ENGINE = {"Bootstrap"}


def classify(text: str, area: str) -> str:
    """Decide the porting tier for one source file."""
    if area == "Render":
        # The whole renderer is being reimplemented on Godot regardless of
        # what any individual file happens to import.
        return TIER_REWRITE
    if HEAVY_NS.search(text):
        return TIER_REWRITE
    if ANY_XNA.search(text):
        return TIER_SHIM
    return TIER_VERBATIM


def area_for(rel: Path) -> str:
    """Map an upstream path to a destination area in the Godot project."""
    key = rel.as_posix()
    best = ""
    for prefix in AREA_MAP:
        if key.startswith(prefix) and len(prefix) > len(best):
            best = prefix
    return AREA_MAP.get(best, "Unsorted")


# --- scanning ---------------------------------------------------------------


def scan_upstream(upstream_src: Path) -> list[dict]:
    """Inventory every upstream C# file with its tier and destination."""
    files: list[dict] = []
    for path in sorted(upstream_src.rglob("*.cs")):
        # Skip generated and build output.
        parts = set(path.parts)
        if {"obj", "bin"} & parts:
            continue
        rel = path.relative_to(upstream_src)
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        area = area_for(rel)
        files.append(
            {
                "upstream": rel.as_posix(),
                "area": area,
                "tier": classify(text, area),
                "lines": text.count("\n") + 1,
                "name": path.name,
            }
        )
    return files


# Folders in the port holding our OWN code rather than ported upstream files.
# They are excluded from match detection: `src/Bootstrap/Main.cs` is a Godot
# entry point written from scratch, and would otherwise be matched against
# upstream's unrelated `Main.cs` purely on filename, inflating the score.
NOT_PORTED_DIRS = {"Bootstrap", "Compat"}


def scan_port(port_src: Path) -> dict[str, list[str]]:
    """Index the ported project by filename.

    Matching is by filename rather than full path: the port is free to
    reorganise folders, and holding it to the upstream layout would be a
    worse outcome than a slightly looser match.

    The trade-off is that a common filename can match the wrong upstream
    file. Excluding our own hand-written folders removes the cases that
    actually occur; anything left is reported honestly as a filename match,
    which the report is explicit about never meaning "works".
    """
    index: dict[str, list[str]] = defaultdict(list)
    if not port_src.is_dir():
        return index
    for path in sorted(port_src.rglob("*.cs")):
        parts = set(path.parts)
        if {"obj", "bin", ".godot"} & parts:
            continue
        rel = path.relative_to(port_src)
        if rel.parts and rel.parts[0] in NOT_PORTED_DIRS:
            continue
        index[path.name].append(rel.as_posix())
    return index


# --- reporting --------------------------------------------------------------


def build_report(files: list[dict], ported: dict[str, list[str]]) -> dict:
    for f in files:
        matches = ported.get(f["name"], [])
        f["ported"] = bool(matches)
        f["port_paths"] = matches

    def summarise(rows: list[dict]) -> dict:
        total = len(rows)
        total_lines = sum(r["lines"] for r in rows)
        done = [r for r in rows if r["ported"]]
        done_lines = sum(r["lines"] for r in done)
        return {
            "files": total,
            "files_done": len(done),
            "lines": total_lines,
            "lines_done": done_lines,
            "pct_files": round(100 * len(done) / total, 1) if total else 0.0,
            "pct_lines": round(100 * done_lines / total_lines, 1) if total_lines else 0.0,
        }

    by_area: dict[str, dict] = {}
    for area in sorted({f["area"] for f in files}):
        rows = [f for f in files if f["area"] == area]
        entry = summarise(rows)
        entry["tiers"] = {
            tier: summarise([r for r in rows if r["tier"] == tier])
            for tier in TIER_ORDER
            if any(r["tier"] == tier for r in rows)
        }
        entry["replaced_by_engine"] = area in REPLACED_BY_ENGINE
        by_area[area] = entry

    by_tier = {
        tier: summarise([f for f in files if f["tier"] == tier]) for tier in TIER_ORDER
    }

    active = [f for f in files if f["area"] not in REPLACED_BY_ENGINE]

    return {
        "schema": "guo/port_status@1",
        "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "overall": summarise(active),
        "by_tier": by_tier,
        "by_area": by_area,
        "files": files,
    }


def _bar(pct: float, width: int = 24) -> str:
    filled = int(round(pct / 100 * width))
    return "#" * filled + "." * (width - filled)


def render_markdown(report: dict, cfg) -> str:
    o = report["overall"]
    lines: list[str] = []
    add = lines.append

    add("# Port status")
    add("")
    add(
        "Generated by `tools/port_audit/run.py`. Do not edit by hand — "
        "re-run `launchers\\pipeline\\03_port_audit.bat`."
    )
    add("")
    add(f"- **Generated:** {report['generated']}")
    add(f"- **Upstream:** `sources/ClassicUO`")
    add(f"- **Port:** `godot/GUO/src`")
    add("")
    add("## Overall")
    add("")
    add(
        f"**{o['files_done']} / {o['files']} files** "
        f"({o['pct_files']}%) · "
        f"**{o['lines_done']:,} / {o['lines']:,} lines** ({o['pct_lines']}%)"
    )
    add("")
    add("```")
    add(f"files  [{_bar(o['pct_files'])}] {o['pct_files']:>5}%")
    add(f"lines  [{_bar(o['pct_lines'])}] {o['pct_lines']:>5}%")
    add("```")
    add("")
    add("Bootstrap is excluded: Godot replaces it outright.")
    add("")

    add("## By porting tier")
    add("")
    add("| Tier | What it means | Files | Done | Lines | Done |")
    add("|---|---|---:|---:|---:|---:|")
    meaning = {
        TIER_VERBATIM: "No FNA reference — copy and renamespace",
        TIER_SHIM: "Only XNA math/colour — needs `GUO.Compat`",
        TIER_REWRITE: "Real FNA binding — reimplement on Godot",
    }
    for tier in TIER_ORDER:
        t = report["by_tier"][tier]
        if not t["files"]:
            continue
        add(
            f"| `{tier}` | {meaning[tier]} | {t['files']} | "
            f"{t['files_done']} ({t['pct_files']}%) | {t['lines']:,} | "
            f"{t['lines_done']:,} ({t['pct_lines']}%) |"
        )
    add("")

    add("## By area")
    add("")
    add("| Area | Files | Done | Lines | Done | verbatim / shim / rewrite |")
    add("|---|---:|---:|---:|---:|---|")
    for area, a in sorted(report["by_area"].items()):
        mix = " / ".join(
            str(a["tiers"].get(t, {}).get("files", 0)) for t in TIER_ORDER
        )
        label = f"`{area}`" + (" _(engine)_" if a["replaced_by_engine"] else "")
        add(
            f"| {label} | {a['files']} | {a['files_done']} ({a['pct_files']}%) | "
            f"{a['lines']:,} | {a['lines_done']:,} ({a['pct_lines']}%) | {mix} |"
        )
    add("")

    # Next actions: the cheapest unported work first.
    add("## Suggested next files")
    add("")
    add(
        "Unported files, cheapest tier first, largest first within a tier — "
        "verbatim files are near-free wins that move the percentage and "
        "unblock the code above them."
    )
    add("")
    for tier in TIER_ORDER:
        pending = [
            f
            for f in report["files"]
            if f["tier"] == tier
            and not f["ported"]
            and f["area"] not in REPLACED_BY_ENGINE
        ]
        if not pending:
            continue
        pending.sort(key=lambda f: -f["lines"])
        add(f"### `{tier}` — {len(pending)} remaining")
        add("")
        for f in pending[:12]:
            add(f"- `{f['upstream']}` → `{f['area']}` ({f['lines']:,} lines)")
        if len(pending) > 12:
            add(f"- _…and {len(pending) - 12} more_")
        add("")

    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="port_audit", description="Score ClassicUO -> Godot port progress."
    )
    parser.add_argument("--upstream", help="path to sources/ClassicUO")
    parser.add_argument("--port", help="path to godot/GUO")
    parser.add_argument("--out", help="write the markdown report here")
    parser.add_argument("--json", dest="json_out", help="also write raw JSON here")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args(argv)

    cfg = load_config()
    upstream = Path(args.upstream) if args.upstream else cfg.upstream
    port = Path(args.port) if args.port else cfg.godot_project

    upstream_src = upstream / "src"
    if not upstream_src.is_dir():
        print(f"[audit] FATAL: upstream source not found: {upstream_src}")
        print("[audit] Run: launchers\\dev\\sync_upstream.bat")
        return 2

    files = scan_upstream(upstream_src)
    ported = scan_port(port / "src")
    report = build_report(files, ported)

    markdown = render_markdown(report, cfg)

    if args.out:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(markdown, encoding="utf-8", newline="\n")
        if not args.quiet:
            print(f"[audit] Report -> {out}")

    if args.json_out:
        jp = Path(args.json_out)
        jp.parent.mkdir(parents=True, exist_ok=True)
        jp.write_text(json.dumps(report, indent=2), encoding="utf-8", newline="\n")
        if not args.quiet:
            print(f"[audit] JSON   -> {jp}")

    if not args.quiet:
        o = report["overall"]
        print(
            f"[audit] {o['files_done']}/{o['files']} files ({o['pct_files']}%), "
            f"{o['lines_done']:,}/{o['lines']:,} lines ({o['pct_lines']}%)"
        )
        for tier in TIER_ORDER:
            t = report["by_tier"][tier]
            if t["files"]:
                print(
                    f"[audit]   {tier:<9} {t['files_done']:>4}/{t['files']:<4} files"
                    f"  {t['lines']:>8,} lines"
                )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
