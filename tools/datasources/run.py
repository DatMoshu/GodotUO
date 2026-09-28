#!/usr/bin/env python3
"""Which client data a run would use (ADR-0021), for the launchers and for people.

    python tools/datasources/run.py check [--bat FILE] [--quiet]

Resolves the ADR-0021 order: a custom data folder (UO_CUSTOM_DATA), then the
UO install (UO_CLIENT_DATA from the environment, else config.local.bat or the
central config, else the platform default), then the first-run wizard.
It prints what was chosen and every candidate that was passed over, and why.

--bat FILE writes a batch fragment a launcher `call`s, which sets
UO_CLIENT_DATA to the chosen install and, for a layered custom folder,
UO_FILES_OVERRIDE to an override file written under build/datasources/.

Exit 0: valid data. Exit 3: no valid data -- run the first-run wizard (the
client opens it; nothing here is an error). Reads only, apart from build/.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.datasources import WIZARD_EXIT, resolve_config, write_override  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["check"])
    ap.add_argument("--bat", type=Path, help="write set lines for a launcher to call")
    ap.add_argument("--quiet", action="store_true", help="only the verdict line")
    args = ap.parse_args()
    cfg = load_config()
    res = resolve_config(cfg)

    override = write_override(res, cfg.build / "datasources" / "files_override.txt")
    print(f"[datasources] {res.message()}")
    if not args.quiet:
        for note in res.notes:
            print(f"[datasources]   passed over: {note}")
        if override:
            print(f"[datasources]   {len(res.overrides)} file(s) layered through {override}")

    if args.bat:
        lines = ["@echo off"]
        if res.ok:
            lines.append(f'set "UO_CLIENT_DATA={res.client_data}"')
        lines.append(f'set "UO_FILES_OVERRIDE={override or ""}"')
        lines.append(f'set "UO_DATA_SOURCE={res.source}"')
        args.bat.parent.mkdir(parents=True, exist_ok=True)
        args.bat.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
    return 0 if res.ok else WIZARD_EXIT


if __name__ == "__main__":
    sys.exit(main())
