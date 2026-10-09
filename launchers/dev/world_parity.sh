#!/usr/bin/env bash
# Compares the editor's UO World view with a logged-in client's frame of the same cell, pixel by pixel, and writes the comparison.
# args: --at <x>,<y> | --season <s> | --windowed
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/world_parity/run.py" "$@"
