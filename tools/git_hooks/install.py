"""Explicitly install/remove GUO's pre-push hook; never overwrite another hook."""
import argparse
from pathlib import Path
import shlex
import subprocess
import sys

MARKER = "# GUO managed pre-push hook v1"


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout.strip()


def install(root, uninstall=False):
    root = Path(root).resolve()
    custom = subprocess.run(["git", "-C", str(root), "config", "--get", "core.hooksPath"], capture_output=True, text=True)
    if custom.returncode == 0:
        raise ValueError("core.hooksPath is already configured; integrate the checker with that hook manager explicitly")
    target = Path(git(root, "rev-parse", "--path-format=absolute", "--git-path", "hooks")) / "pre-push"
    if target.is_symlink():
        raise ValueError("Existing pre-push is a link; refusing to replace it")
    if target.exists() and MARKER not in target.read_text(encoding="utf-8", errors="replace").splitlines()[:2]:
        raise ValueError("An unmanaged pre-push hook exists; leave it intact and integrate check.py manually")
    if uninstall:
        target.unlink(missing_ok=True)
        print("GUO pre-push hook removed (other hooks unchanged)")
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    python = shlex.quote(sys.executable.replace("\\", "/"))
    script = f'''#!/bin/sh
{MARKER}
root=$(git rev-parse --show-toplevel) || exit 1
if [ -n "$UO_PYTHON" ]; then python="$UO_PYTHON"; else python={python}; fi
exec "$python" "$root/tools/git_hooks/check.py" "$@"
'''
    target.write_text(script, encoding="utf-8", newline="\n")
    target.chmod(target.stat().st_mode | 0o111)
    print("GUO pre-push hook installed. Git worktrees share this repository hook.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--uninstall", action="store_true")
    args = parser.parse_args()
    install(git(Path.cwd(), "rev-parse", "--show-toplevel"), args.uninstall)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print("[git-hooks]", exc, file=sys.stderr)
        raise SystemExit(1)
