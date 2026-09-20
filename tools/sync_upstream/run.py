#!/usr/bin/env python3
"""Keep the read-only ClassicUO reference current, and surface what changed.

A port that never looks upstream again inherits every bug ClassicUO has
already fixed. This tool clones the reference if it is missing, updates it,
and — crucially — reports which upstream commits touched files the port has
*already* ported, because those are the changes that silently rot.

    python tools/sync_upstream/run.py            update and report drift
    python tools/sync_upstream/run.py --pin      mark current upstream as reviewed
    python tools/sync_upstream/run.py --no-fetch report without touching network

The pin lives in docs/upstream/UPSTREAM_PIN.json and is committed, so the
whole team shares one answer to "what have we reviewed up to?".

Nothing under sources/ is ever edited by this tool or by the port. It is
reference material only.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

UPSTREAM_URL = "https://github.com/ClassicUO/ClassicUO.git"
PIN_RELATIVE = Path("docs/upstream/UPSTREAM_PIN.json")


def git(repo: Path, *args: str, check: bool = True) -> str:
    """Run git in `repo` and return stdout."""
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if check and result.returncode != 0:
        raise RuntimeError(
            f"git {' '.join(args)} failed ({result.returncode}):\n{result.stderr.strip()}"
        )
    return result.stdout.strip()


def clone(dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"[sync] Cloning {UPSTREAM_URL} -> {dest}")
    subprocess.run(
        [
            "git",
            "clone",
            "--recurse-submodules",
            "--shallow-submodules",
            UPSTREAM_URL,
            str(dest),
        ],
        check=True,
    )


def deepen_if_shallow(repo: Path) -> None:
    """A --depth 1 clone cannot show history; unshallow on first real sync."""
    shallow = (repo / ".git" / "shallow").exists()
    if not shallow:
        return
    print("[sync] Reference is a shallow clone; fetching full history for diffing...")
    subprocess.run(
        ["git", "-C", str(repo), "fetch", "--unshallow"],
        check=False,
    )


def load_pin(root: Path) -> dict | None:
    path = root / PIN_RELATIVE
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def write_pin(root: Path, commit: str, subject: str, when: str) -> Path:
    path = root / PIN_RELATIVE
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schema": "guo/upstream_pin@1",
        "repo": UPSTREAM_URL,
        "commit": commit,
        "subject": subject,
        "committed": when,
        "reviewed_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "note": (
            "Upstream reviewed up to this commit. Commits after it have not "
            "yet been assessed for porting. Update with: "
            "launchers\\dev\\sync_upstream.bat --pin"
        ),
    }
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8", newline="\n")
    return path


def ported_filenames(port_src: Path) -> set[str]:
    """Filenames present in the port, used to spot high-risk upstream drift."""
    if not port_src.is_dir():
        return set()
    return {
        p.name
        for p in port_src.rglob("*.cs")
        if not ({"obj", "bin", ".godot"} & set(p.parts))
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="sync_upstream",
        description="Update the ClassicUO reference and report upstream drift.",
    )
    parser.add_argument("--root", help="repo root (auto-detected by default)")
    parser.add_argument(
        "--pin",
        action="store_true",
        help="record current upstream HEAD as reviewed",
    )
    parser.add_argument(
        "--no-fetch", action="store_true", help="do not contact the network"
    )
    parser.add_argument(
        "--limit", type=int, default=25, help="max commits to list (default 25)"
    )
    args = parser.parse_args(argv)

    cfg = load_config(Path(args.root) if args.root else None)
    upstream = cfg.upstream

    if not upstream.is_dir():
        clone(upstream)
    elif not args.no_fetch:
        deepen_if_shallow(upstream)
        print(f"[sync] Fetching {upstream}")
        git(upstream, "fetch", "--all", "--tags", check=False)

    branch = git(upstream, "rev-parse", "--abbrev-ref", "HEAD", check=False) or "main"
    if not args.no_fetch:
        # Fast-forward the reference. It is read-only, so this can never
        # conflict with local work.
        git(upstream, "merge", "--ff-only", f"origin/{branch}", check=False)

    head = git(upstream, "rev-parse", "HEAD")
    head_subject = git(upstream, "log", "-1", "--format=%s")
    head_date = git(upstream, "log", "-1", "--format=%cI")

    print(f"[sync] Upstream HEAD : {head[:12]}  {head_date}")
    print(f"[sync]                {head_subject}")

    pin = load_pin(cfg.root)

    if args.pin:
        path = write_pin(cfg.root, head, head_subject, head_date)
        print(f"[sync] Pinned reviewed-up-to {head[:12]} -> {path}")
        return 0

    if pin is None:
        print()
        print("[sync] No review pin recorded yet.")
        print("[sync] Establish the baseline with:")
        print("[sync]     launchers\\dev\\sync_upstream.bat --pin")
        return 0

    base = pin["commit"]
    if base == head:
        print("[sync] Up to date with the reviewed pin. Nothing to assess.")
        return 0

    rng = f"{base}..{head}"
    raw = git(upstream, "log", "--format=%H%x1f%cI%x1f%s", rng, check=False)
    commits = []
    for line in raw.splitlines():
        if not line.strip():
            continue
        sha, when, subject = line.split("\x1f", 2)
        commits.append({"sha": sha, "date": when, "subject": subject})

    if not commits:
        print(f"[sync] No commits between pin {base[:12]} and HEAD.")
        return 0

    changed = git(upstream, "diff", "--name-only", rng, check=False).splitlines()
    changed_cs = [c for c in changed if c.endswith(".cs")]
    already = ported_filenames(cfg.godot_project / "src")
    hot = sorted({c for c in changed_cs if Path(c).name in already})

    print()
    print(f"[sync] {len(commits)} commit(s) since the reviewed pin {base[:12]}")
    print(f"[sync] {len(changed_cs)} C# file(s) changed upstream")
    print()

    for c in commits[: args.limit]:
        print(f"  {c['sha'][:10]}  {c['date'][:10]}  {c['subject'][:88]}")
    if len(commits) > args.limit:
        print(f"  ... and {len(commits) - args.limit} more")

    if hot:
        print()
        print(f"[sync] !! {len(hot)} changed file(s) are ALREADY PORTED.")
        print("[sync]    These are the ones that will silently drift:")
        for path in hot[:30]:
            print(f"      - {path}")
        if len(hot) > 30:
            print(f"      ... and {len(hot) - 30} more")

    print()
    print("[sync] After assessing these, record the review with:")
    print("[sync]     launchers\\dev\\sync_upstream.bat --pin")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
