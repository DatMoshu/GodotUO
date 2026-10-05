#!/usr/bin/env python3
"""Archive a worktree's gate evidence before the worktree is removed.

`git worktree remove` deletes ignored files, and a worker's smoke logs, reports
and captures live in its ignored build/ folder. This copies them to the
evidence folder (setting UO_EVIDENCE_DIR; default: the main checkout's
build/director_evidence), writes a sha256sum-compatible MANIFEST.sha256 and
checks the copy by reading it back. The source is never changed or deleted.

Usage:
    python tools/evidence_archive/run.py archive WORKTREE NAME [--paths P ...] [--max-mb N]
    python tools/evidence_archive/run.py verify NAME
    python tools/evidence_archive/run.py list

Exit codes: 0 ok, 1 refused or a hash mismatch, 2 bad input.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import find_repo_root, load_config  # noqa: E402

MANIFEST = "MANIFEST.sha256"
SOURCE = "SOURCE.json"
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def git(worktree: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(worktree), *args], capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else ""


def linked_worktrees(worktree: Path) -> list[Path]:
    """Every linked worktree of the repository (the main checkout excluded)."""
    paths = [Path(line[len("worktree "):]).resolve()
             for line in git(worktree, "worktree", "list", "--porcelain").splitlines()
             if line.startswith("worktree ")]
    return paths[1:]  # git lists the main checkout first


def inside(path: Path, folder: Path) -> bool:
    try:
        path.resolve().relative_to(folder.resolve())
        return True
    except ValueError:
        return False


def evidence_dir(arg: str | None) -> Path:
    if arg:
        return Path(arg).resolve()
    return load_config(find_repo_root()).evidence_dir.resolve()


def files_under(root: Path, rels: list[str]) -> list[Path]:
    found: list[Path] = []
    for rel in rels:
        p = (root / rel).resolve()
        if not inside(p, root):
            raise ValueError(f"{rel} is outside the worktree")
        if p.is_file():
            found.append(p)
        elif p.is_dir():
            found.extend(sorted(f for f in p.rglob("*") if f.is_file()))
    return found


def write_manifest(dest: Path) -> int:
    lines = []
    for f in sorted(dest.rglob("*")):
        if f.is_file() and f.name not in (MANIFEST,):
            lines.append(f"{sha256(f)} *{f.relative_to(dest).as_posix()}")
    (dest / MANIFEST).write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return len(lines)


def check(dest: Path) -> tuple[int, list[str]]:
    manifest = dest / MANIFEST
    if not manifest.is_file():
        return 0, [f"no {MANIFEST} in {dest}"]
    ok, bad = 0, []
    for line in manifest.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        digest, _, rel = line.partition(" *")
        f = dest / rel
        if not f.is_file():
            bad.append(f"missing {rel}")
        elif sha256(f) != digest:
            bad.append(f"changed {rel}")
        else:
            ok += 1
    return ok, bad


def cmd_archive(a: argparse.Namespace) -> int:
    worktree = Path(a.worktree).resolve()
    if not NAME_RE.match(a.name):
        print(f"[evidence] bad name '{a.name}' (letters, digits, . _ -)", file=sys.stderr)
        return 2
    if not worktree.is_dir():
        print(f"[evidence] no such folder {worktree}", file=sys.stderr)
        return 2
    root = evidence_dir(a.dir)
    dest = root / a.name
    for linked in linked_worktrees(worktree):
        if inside(root, linked):
            print(f"[evidence] refused: the evidence folder {root} is inside the worktree {linked}, "
                  "and removing that worktree would delete it", file=sys.stderr)
            return 1
    if inside(root, worktree):
        print(f"[evidence] refused: the evidence folder {root} is inside the source {worktree}", file=sys.stderr)
        return 1
    if dest.exists():
        print(f"[evidence] refused: {dest} already exists; pick another name", file=sys.stderr)
        return 1
    try:
        files = files_under(worktree, a.paths or ["build"])
    except ValueError as e:
        print(f"[evidence] {e}", file=sys.stderr)
        return 2
    if not files:
        print(f"[evidence] nothing to archive under {', '.join(a.paths or ['build'])}", file=sys.stderr)
        return 1
    total = sum(f.stat().st_size for f in files)
    if total > a.max_mb * 1024 * 1024:
        print(f"[evidence] refused: {total / 1048576:.1f} MB exceeds --max-mb {a.max_mb}; "
              "narrow it with --paths", file=sys.stderr)
        return 1

    source_hashes = {}
    for f in files:
        rel = f.relative_to(worktree)
        target = dest / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(f, target)
        source_hashes[rel.as_posix()] = sha256(f)
    (dest / SOURCE).write_text(json.dumps({
        "format": 1,
        "worktree": worktree.name,
        "branch": git(worktree, "rev-parse", "--abbrev-ref", "HEAD"),
        "commit": git(worktree, "rev-parse", "HEAD"),
        "paths": a.paths or ["build"],
        "files": len(files),
        "bytes": total,
        "archived": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }, indent=2) + "\n", encoding="utf-8")
    count = write_manifest(dest)

    ok, bad = check(dest)
    mismatched = [rel for rel, digest in source_hashes.items() if sha256(dest / rel) != digest]
    if bad or mismatched:
        for line in bad + [f"differs from source {rel}" for rel in mismatched]:
            print(f"[evidence] {line}", file=sys.stderr)
        return 1
    manifest_hash = sha256(dest / MANIFEST)
    print(json.dumps({"archive": str(dest), "files": count, "verified": ok,
                      "manifest_sha256": manifest_hash}))
    print(f"[evidence] {ok}/{count} verified; the source is unchanged. Remove the worktree only "
          "after its owner says it is safe.", file=sys.stderr)
    return 0


def cmd_verify(a: argparse.Namespace) -> int:
    dest = evidence_dir(a.dir) / a.name
    ok, bad = check(dest)
    for line in bad:
        print(f"[evidence] {line}", file=sys.stderr)
    print(json.dumps({"archive": str(dest), "verified": ok, "problems": len(bad)}))
    return 1 if bad or ok == 0 else 0


def cmd_list(a: argparse.Namespace) -> int:
    root = evidence_dir(a.dir)
    for d in sorted(p for p in root.iterdir() if p.is_dir()) if root.is_dir() else []:
        print(f"{d.name}\t{'manifest' if (d / MANIFEST).is_file() else 'no manifest'}")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--dir", help="evidence folder (overrides UO_EVIDENCE_DIR)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("archive", help="copy a worktree's evidence and verify it")
    s.add_argument("worktree")
    s.add_argument("name")
    s.add_argument("--paths", nargs="+", help="worktree-relative files or folders (default: build)")
    s.add_argument("--max-mb", type=int, default=2048)
    s.set_defaults(fn=cmd_archive)
    s = sub.add_parser("verify", help="re-check an archive against its manifest")
    s.add_argument("name")
    s.set_defaults(fn=cmd_verify)
    s = sub.add_parser("list", help="list archives")
    s.set_defaults(fn=cmd_list)
    a = p.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
