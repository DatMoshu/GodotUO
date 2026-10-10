#!/usr/bin/env bash
# Checks every launcher is a .bat/.sh pair with a description line, the right line endings and the executable bit, and prints each problem.
# args: --list | --json
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/launcher_lint/run.py" "$@"
