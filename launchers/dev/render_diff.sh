#!/usr/bin/env bash
# Compares the ClassicUO and GUO render dumps named NAME and writes build/render_dump/NAME/diff.md.
# args: <name> | --list
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/render_diff/run.py" "$@"
