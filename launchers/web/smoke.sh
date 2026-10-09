#!/usr/bin/env bash
# Exports and serves the web build, loads it in a headless browser and fails unless the engine prints its first console line.
# args: --no-export
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/web/run.py" smoke "$@"
