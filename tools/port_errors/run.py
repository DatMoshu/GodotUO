#!/usr/bin/env python3
"""Measure how much of the port is left, by building EVERY area at once.

`GUO.csproj` excludes the areas that do not compile yet (the staged build).
That keeps the tree buildable, and it also hides the remaining work, so the
honest progress number is: turn every exclusion off and count what the
compiler says.

Two things this gets right that doing it by hand does not.

  * It forces a full rebuild. An incremental `dotnet build` can skip the
    compile and report a handful of errors from a partial pass -- which reads
    like real progress and is not. Measured on the same tree, the same day:
    9 errors incrementally, 1,600 on a rebuild. Every "N errors left" number
    recorded before this tool existed was measured the first way and is wrong.
  * It groups the errors by the symbol that is actually missing, so the output
    is a list of things left to port rather than a list of call sites. One
    unported class is worth one line here, not five hundred.
  * It de-duplicates. MSBuild prints each error once per target pass, so the
    raw line count is roughly double the real one.

One thing it still cannot get right, so read the number with this in mind.
Roslyn binds declarations before method bodies, and it does not bind bodies at
all if a declaration failed. So a single unported TYPE used in a field or a
parameter hides every error inside every method in the build. Measured: with
two gumps referring to an unported WorldMapGump, the whole tree reported
9 errors; with those two files moved aside, the same tree reported 577. A
small number here therefore means either "nearly done" or "one missing type is
masking everything", and the only way to tell them apart is to clear the
handful it reports and measure again. The count is a ratchet to drive down,
not a distance to the finish.

Usage:
    python tools/port_errors/run.py [--top 25] [--raw]

Through the launcher:
    launchers\\dev\\port_errors.bat
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

ERROR_LINE = re.compile(
    # Not just CS: the Godot source generators report their own errors (GD0001
    # and friends), and one of those stops the build exactly as a CS error
    # does. Counting only CS codes would report a clean board on a build that
    # produced no assembly.
    r"^(?P<file>.+?)\((?P<line>\d+),\d+\): error (?P<code>[A-Z]+\d+): (?P<text>.*?)(?: \[.*\])?$"
)

# The symbol an error is really about. Whatever the message shape, what the
# port cares about is "which name does not exist yet".
SYMBOL = [
    re.compile(r"The name '(?P<s>[^']+)' does not exist"),
    re.compile(r"The type or namespace name '(?P<s>[^']+)' could not be found"),
    re.compile(r"The type or namespace name '(?P<s>[^']+)' does not exist"),
    re.compile(r"The type name '(?P<s>[^']+)' does not exist"),
    re.compile(r"'(?P<owner>[^']+)' does not contain a definition for '(?P<s>[^']+)'"),
    re.compile(r"'(?P<owner>[^']+)' does not contain a constructor"),
    re.compile(r"No overload for method '(?P<s>[^']+)'"),
    re.compile(r"'(?P<s>[^']+)' is an ambiguous reference"),
]


def symbol_for(text: str) -> str:
    for pattern in SYMBOL:
        m = pattern.search(text)
        if not m:
            continue
        groups = m.groupdict()
        if groups.get("owner") and groups.get("s"):
            return f"{groups['owner']}.{groups['s']}"
        if groups.get("owner"):
            return f"{groups['owner']} ctor"
        return groups["s"]
    return text[:60]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="port_errors")
    ap.add_argument("--top", type=int, default=25, help="clusters to print")
    ap.add_argument("--raw", action="store_true", help="print every error line")
    args = ap.parse_args(argv)

    cfg = load_config()
    csproj = cfg.godot_project / "GUO.csproj"

    if not csproj.is_file():
        print(f"[port-errors] FATAL: not found: {csproj}")
        return 2

    print("[port-errors] rebuilding every area (this is not the staged build)")

    # -t:Rebuild, not build: see the module docstring.
    proc = subprocess.run(
        [
            "dotnet", "build", str(csproj),
            "-t:Rebuild",
            "-p:GuoAllAreas=true",
            "-v", "q", "--nologo",
        ],
        capture_output=True,
        text=True,
    )

    seen: set[tuple[str, str, str]] = set()
    clusters: Counter[str] = Counter()
    files: Counter[str] = Counter()
    codes: Counter[str] = Counter()
    total = 0

    for line in proc.stdout.splitlines():
        m = ERROR_LINE.match(line.strip())
        if not m:
            continue

        # MSBuild prints each error once per target pass; de-duplicate on
        # (file, line, text) so the count is errors, not print-outs.
        key = (m["file"], m["line"], m["text"])
        if key in seen:
            continue
        seen.add(key)

        total += 1
        codes[m["code"]] += 1
        clusters[symbol_for(m["text"])] += 1
        try:
            files[str(Path(m["file"]).relative_to(cfg.godot_project))] += 1
        except ValueError:
            files[m["file"]] += 1

        if args.raw:
            print(f"  {m['file']}({m['line']}): {m['code']}: {m['text']}")

    if total == 0 and proc.returncode != 0:
        # The build failed for something that is not a C# error -- a broken
        # project file, a missing SDK. Saying "0 errors" here would be the
        # worst possible answer.
        print("[port-errors] the build failed without reporting a C# error:")
        print(proc.stdout.strip()[-2000:])
        print(proc.stderr.strip()[-2000:])
        return 2

    if total == 0:
        print("[port-errors] 0 errors with every area enabled.")
        print("[port-errors] The staged-build exclusions can go.")
        return 0

    print(f"\n[port-errors] {total} errors, {len(clusters)} distinct missing symbols\n")

    if total < 50:
        print("  NOTE: a low count can mean one unported type is masking the")
        print("  rest -- Roslyn skips every method body once a declaration")
        print("  fails. Clear these and measure again before believing it.\n")

    print(f"  {'missing symbol':<44} errors")
    print(f"  {'-' * 44} ------")
    for symbol, count in clusters.most_common(args.top):
        print(f"  {symbol:<44} {count:>6}")

    if len(clusters) > args.top:
        print(f"  ... and {len(clusters) - args.top} more")

    print(f"\n  {'worst files':<44} errors")
    print(f"  {'-' * 44} ------")
    for name, count in files.most_common(10):
        print(f"  {name:<44} {count:>6}")

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
