#!/usr/bin/env python3
"""privacy_scan - fail if a tracked file names someone's machine, network or devices.

GUO is public. What belongs to one person's setup (a LAN address, a phone's
adb serial, a personal email, a home folder) lives in the gitignored
launchers/_shared/config.local.bat, never in a tracked file. This scan runs
in CI and before a push:

    python tools/privacy_scan/run.py            scan every tracked file
    python tools/privacy_scan/run.py --staged   scan what is staged for commit, as staged

Patterns that are always wrong are built in. Values only you know (your
account name, your device serials) go one per line in
tools/privacy_scan/deny.local.txt, which is gitignored; CI cannot know them,
so run the scan locally before pushing. A line that is a legitimate example
is excused by adding a regex to tools/privacy_scan/allow.txt.

Exit 0 when clean, 1 with a list of hits.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent

# Always wrong in a tracked file. Each: (name, regex).
BUILTIN = [
    # RFC 1918 addresses. 127.0.0.1 and 0.0.0.0 are fine and do not match.
    ("private IPv4", r"\b(?:192\.168|10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b"),
    # Any email except GitHub's noreply form and the example domains.
    ("email", r"\b[A-Za-z0-9._%+-]+@(?!users\.noreply\.github\.com\b|noreply\.|example\.(?:com|org)\b)"
              r"[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
    # A Windows home folder or a Linux/mac one with a real-looking user name.
    ("home folder", r"[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b|<)[A-Za-z0-9._-]+"),
    ("home folder", r"(?<![\w.])/(?:home|Users)/(?!deck\b|user\b|runner\b|<)[a-z][a-z0-9._-]+/"),
]

# Never scanned: upstream's own tree, binary assets, and the scan's own lists.
SKIP_PREFIXES = ("sources/", "tools/privacy_scan/")
SKIP_SUFFIXES = (".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".icns", ".ogv", ".ogg",
                 ".wav", ".mp3", ".mp4", ".ttf", ".otf", ".woff", ".woff2", ".psd", ".blend", ".glb")

# Compiled programs are never tracked (.gitignore says so): a release build embeds the
# builder's home folder. gdcef.dll reached GitHub this way on 2026-10-05.
BINARY_PROGRAM_SUFFIXES = (".dll", ".exe", ".so", ".dylib", ".pdb", ".lib", ".a", ".node", ".pyd")

# Byte patterns for files that are not UTF-8 text: a Windows home folder as ASCII and as
# UTF-16LE (how Windows programs store paths), and the local deny list in both encodings.
BINARY_HOME = [re.compile(rb"[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[A-Za-z0-9._-]+"),
               re.compile(rb"[A-Za-z]\x00:\x00(?:[\\/]\x00)+U\x00s\x00e\x00r\x00s\x00(?:[\\/]\x00)+(?:[A-Za-z0-9._-]\x00){2,}")]


def utf16(value: str) -> bytes:
    return value.encode("utf-16-le")


def scan_bytes(rel: str, data: bytes, deny: list[str]) -> list[str]:
    """Hits inside a binary file: home folders and deny-list values, ASCII or UTF-16LE."""
    out = []
    for rx in BINARY_HOME:
        m = rx.search(data)
        if m:
            out.append(f"{rel}: home folder in binary: {m.group(0)[:60]!r}")
            break
    low = data.lower()
    for value in deny:
        v = value.lower()
        if v.encode("utf-8") in low or utf16(v) in low:
            out.append(f"{rel}: local deny list in binary: {value}")
    return out


def read_list(path: Path) -> list[str]:
    if not path.exists():
        return []
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.append(line)
    return out


def tracked_files(root: Path, staged: bool) -> list[str]:
    cmd = ["git", "diff", "--cached", "--name-only", "--diff-filter=ACMR"] if staged \
        else ["git", "ls-files"]
    out = subprocess.run(cmd, cwd=root, capture_output=True, text=True, check=True).stdout
    return [f for f in out.splitlines() if f]


def read_staged(root: Path, rel: str) -> bytes | None:
    """The bytes the next commit will hold for rel: its blob in the index, not the working file."""
    result = subprocess.run(["git", "cat-file", "blob", f":{rel}"], cwd=root, capture_output=True)
    return result.stdout if result.returncode == 0 else None


def read_working(root: Path, rel: str) -> bytes | None:
    try:
        return (root / rel).read_bytes()
    except (FileNotFoundError, IsADirectoryError, PermissionError):
        return None


def decode_text(data: bytes) -> str | None:
    """Text of a file in UTF-8, or in UTF-16 when it starts with a byte order mark; None for anything else."""
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        try:
            return data.decode("utf-16")
        except UnicodeDecodeError:
            return None
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return None


def scan(root: Path, staged: bool, here: Path = HERE) -> list[str]:
    rules = [(name, re.compile(rx)) for name, rx in BUILTIN]
    deny = read_list(here / "deny.local.txt")
    for value in deny:
        rules.append(("local deny list", re.compile(re.escape(value), re.IGNORECASE)))
    allow = [re.compile(rx) for rx in read_list(here / "allow.txt")]

    read = read_staged if staged else read_working
    hits = []
    for rel in tracked_files(root, staged):
        if rel.startswith(SKIP_PREFIXES) or rel.lower().endswith(SKIP_SUFFIXES):
            continue
        if rel.lower().endswith(BINARY_PROGRAM_SUFFIXES):
            hits.append(f"{rel}: compiled program tracked; build it locally or fetch it (see .gitignore)")
            continue
        data = read(root, rel)
        if data is None:
            continue
        text = decode_text(data)
        if text is None:
            hits.extend(scan_bytes(rel, data, deny))
            continue
        for n, line in enumerate(text.splitlines(), 1):
            for name, rx in rules:
                m = rx.search(line)
                if m and not any(a.search(line) for a in allow):
                    hits.append(f"{rel}:{n}: {name}: {m.group(0)}")
    return hits


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--staged", action="store_true",
                    help="scan only files staged for commit, as staged (the index, not the working copy)")
    args = ap.parse_args(argv)

    hits = scan(ROOT, args.staged)
    if hits:
        print(f"[privacy_scan] {len(hits)} hit(s). Move these values to "
              "launchers/_shared/config.local.bat, or excuse a real example in "
              "tools/privacy_scan/allow.txt:")
        for h in hits:
            print("  " + h)
        return 1
    print("[privacy_scan] clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
