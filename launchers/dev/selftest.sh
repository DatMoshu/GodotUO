#!/usr/bin/env bash
# Runs the Python tool self-tests CI runs (no client data, no shard) and prints a pass or fail line per test file.
# args: --list | --only <text>
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/selftest/run.py" "$@"
