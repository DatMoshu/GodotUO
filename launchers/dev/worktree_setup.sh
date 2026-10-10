#!/usr/bin/env bash
# Readies a fresh git worktree: copies your local config across, checks the engine and upstream resolve, and runs one headless import.
# args: --no-import
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/worktree_setup/run.py" "$@"
