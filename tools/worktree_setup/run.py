#!/usr/bin/env python3
"""Make a fresh git worktree ready to build, run and smoke, with no directory links.

A worktree gets only what git tracks. The engine (tools/godot) and the upstream
reference (sources/ClassicUO) are gitignored, and so are the user's own
settings. Instead of linking those folders in, the settings UO_GODOT_HOME and
UO_UPSTREAM_DIR default, in a worktree, to the main checkout's copies
(launchers/_shared/common.bat, tools/guo/config.py and
tools/msbuild/UpstreamDir.props all resolve them the same way). This tool:

  1. copies the gitignored per-user files a worktree needs from the main
     checkout, when the worktree lacks them -- never overwriting one:
         launchers/_shared/config.local.bat   (your paths)
         tools/privacy_scan/deny.local.txt    (your private deny list)
  2. checks that the engine and the upstream reference resolve,
  3. runs the console engine once with --headless --import, so the project's
     .godot/ import cache exists before the first editor or smoke run,
  4. prints a summary. Exit 0 when everything resolved and the import passed.

Usage:
    python tools/worktree_setup/run.py [--no-import]

Or through the launcher:
    launchers\\dev\\worktree_setup.bat
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import find_repo_root, load_config, main_checkout  # noqa: E402

# Gitignored per-user files a worktree needs, relative to the checkout root.
LOCAL_FILES = (
    Path("launchers/_shared/config.local.bat"),
    Path("tools/privacy_scan/deny.local.txt"),
)

IMPORT_TIMEOUT = 1800


def where(path: Path, root: Path, main: Path | None) -> str:
    """Say whether a resolved folder is this checkout's, the main checkout's, or configured."""
    resolved = path.resolve()
    if resolved.is_relative_to(root.resolve()):
        return "this checkout"
    if main is not None and resolved.is_relative_to(main.resolve()):
        return "main checkout"
    return "configured"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--no-import", action="store_true", help="skip the headless import")
    args = parser.parse_args()

    root = find_repo_root()
    main_root = main_checkout(root)
    rows: list[tuple[str, str, str]] = []
    failed = False

    print(f"[worktree_setup] checkout : {root}")
    print(f"[worktree_setup] main     : {main_root or '(this is the main checkout)'}")

    # 1. Per-user files. Copied, never linked, never overwritten, never committed
    #    (both are gitignored).
    for rel in LOCAL_FILES:
        target = root / rel
        if target.is_file():
            rows.append(("ok", str(rel), "present"))
        elif main_root is not None and (main_root / rel).is_file():
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(main_root / rel, target)
            rows.append(("ok", str(rel), "copied from the main checkout"))
        else:
            # Optional: a missing file only means the defaults apply.
            rows.append(("--", str(rel), "absent (defaults apply)"))

    # 2. Resolve, after the copy so config.local.bat is honoured.
    cfg = load_config(root)
    console = cfg.godot_console_exe
    if console.is_file():
        rows.append(("ok", "engine", f"{console} ({where(console, root, main_root)})"))
    else:
        failed = True
        rows.append(("FAIL", "engine", f"not found: {console}; fetch it in the main checkout "
                     "(launchers\\dev\\fetch_godot.bat) or set UO_GODOT_HOME"))
    if cfg.upstream.is_dir():
        rows.append(("ok", "upstream", f"{cfg.upstream} ({where(cfg.upstream, root, main_root)})"))
    else:
        failed = True
        rows.append(("FAIL", "upstream", f"not found: {cfg.upstream}; run launchers\\dev\\sync_upstream.bat "
                     "in the main checkout or set UO_UPSTREAM_DIR"))

    # 3. One headless import, so .godot/ exists before the editor or a smoke opens the project.
    if args.no_import:
        rows.append(("--", "import", "skipped (--no-import)"))
    elif not console.is_file():
        rows.append(("--", "import", "skipped (no engine)"))
    else:
        log = cfg.build / "worktree_setup" / "import.log"
        log.parent.mkdir(parents=True, exist_ok=True)
        cmd = [str(console), "--headless", "--path", str(cfg.godot_project), "--import"]
        print(f"[worktree_setup] importing: {' '.join(cmd)}")
        try:
            with log.open("w", encoding="utf-8", errors="replace") as out:
                code = subprocess.run(cmd, stdout=out, stderr=subprocess.STDOUT, timeout=IMPORT_TIMEOUT).returncode
        except subprocess.TimeoutExpired:
            code = None
        if code == 0:
            rows.append(("ok", "import", f"headless import passed (log: {log})"))
        else:
            failed = True
            rows.append(("FAIL", "import", f"{'timed out' if code is None else f'exit {code}'} (log: {log})"))

    # 4. Summary.
    print("[worktree_setup] summary:")
    for status, what, detail in rows:
        print(f"[worktree_setup]   {status:<4} {what:<38} {detail}")
    print(f"[worktree_setup] {'FAIL' if failed else 'OK'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
