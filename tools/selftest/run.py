#!/usr/bin/env python3
"""Run the Python tool self-tests CI runs, one after another, and say which failed.

    python tools/selftest/run.py                 every one
    python tools/selftest/run.py --list          the list, nothing run
    python tools/selftest/run.py --only store    only those whose path holds "store"

The list is read from .github/workflows/ci.yml, every `python tools/.../test_*.py`
line in it, so CI and this launcher never disagree about what the self-tests
are. None needs client data, a shard or the engine.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
_TEST_RE = re.compile(r"\bpython3?\s+(tools/[\w./-]*test_[\w-]+\.py)\b")


def selftests(root: Path) -> list[str]:
    found: list[str] = []
    for line in (root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8").splitlines():
        if line.lstrip().startswith("#"):
            continue
        for match in _TEST_RE.finditer(line):
            if match.group(1) not in found:
                found.append(match.group(1))
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--list", action="store_true", help="print the self-tests and run nothing")
    parser.add_argument("--only", default="", help="run only the self-tests whose path contains this text")
    args = parser.parse_args(argv)
    tests = [t for t in selftests(ROOT) if args.only in t]
    if args.list:
        print("\n".join(tests))
        return 0
    if not tests:
        print(f"[selftest] nothing matches --only {args.only!r}", file=sys.stderr)
        return 1
    failed = []
    for test in tests:
        if not (ROOT / test).is_file():
            print(f"[selftest] FAIL {test}: missing")
            failed.append(test)
            continue
        started = time.monotonic()
        result = subprocess.run([sys.executable, test], cwd=ROOT, capture_output=True, text=True,
                                encoding="utf-8", errors="replace")
        seconds = time.monotonic() - started
        if result.returncode:
            print(f"[selftest] FAIL {test} ({seconds:.1f} s)")
            print((result.stdout + result.stderr).rstrip()[-4000:])
            failed.append(test)
        else:
            print(f"[selftest] pass {test} ({seconds:.1f} s)")
    print(f"[selftest] {len(tests) - len(failed)}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
