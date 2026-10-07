#!/usr/bin/env python3
"""Install and run a ModernUO shard on a Linux host, from a profile (docs/data_formats.md section 35).

    python tools/muo_shard/run.py validate --profile P
    python tools/muo_shard/run.py plan bootstrap|deploy|status --profile P [--pin SHA]

`plan` prints a bash script to stdout and runs nothing. Review it, then run it on
the host:

    python tools/muo_shard/run.py plan deploy --profile P > deploy.sh
    ssh HOST 'sudo bash -s' < deploy.sh

Exit codes: 0 ok, 1 the profile is refused, 2 bad usage.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import plans  # noqa: E402
import shardprofile  # noqa: E402


def pinned_ref() -> str | None:
    """UO_SHARD_REF: the environment, else launchers/_shared/config.bat (the shared default)."""
    if os.environ.get("UO_SHARD_REF"):
        return os.environ["UO_SHARD_REF"]
    cfg = HERE.parents[1] / "launchers" / "_shared" / "config.bat"
    try:
        m = re.search(r'set "UO_SHARD_REF=([0-9a-f]{40})"', cfg.read_text(encoding="utf-8"))
    except OSError:
        return None
    return m.group(1) if m else None


def out(text: str) -> None:
    sys.stdout.buffer.write(text.encode("utf-8"))
    sys.stdout.buffer.flush()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    v = sub.add_parser("validate", help="check a profile; exit 1 and say why if it is refused")
    v.add_argument("--profile", required=True)
    pl = sub.add_parser("plan", help="print the shell script for a verb; runs nothing")
    pl.add_argument("verb", choices=sorted(plans.VERBS))
    pl.add_argument("--profile", required=True)
    pl.add_argument("--pin", help="deploy this commit instead of the profile's ref (40 hex)")
    a = ap.parse_args(argv)

    try:
        prof = shardprofile.load(a.profile)
        if a.cmd == "validate":
            print(f"[muo_shard] {prof.file.name}: ok (id {prof.id}, port {prof.data['listen']['port']})")
            return 0
        if a.verb == "deploy":
            src = prof.data["server"]["source"]
            ref = pinned_ref()
            if a.pin is None and src["kind"] == "git" and ref and src["ref"] != ref:
                print(f"[muo_shard] note: the profile pins {src['ref'][:9]} but UO_SHARD_REF is {ref[:9]}", file=sys.stderr)
            out(plans.deploy(prof, a.pin))
        else:
            out(plans.VERBS[a.verb](prof))
        return 0
    except shardprofile.ProfileError as e:
        for problem in e.problems:
            print(f"[muo_shard] {problem}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
