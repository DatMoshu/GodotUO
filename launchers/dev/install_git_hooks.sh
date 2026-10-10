#!/usr/bin/env bash
# Installs the pre-push docs, launcher and privacy checks into this checkout's git hooks and prints what it installed.
# args: --remove
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/git_hooks/install.py" "$@"
