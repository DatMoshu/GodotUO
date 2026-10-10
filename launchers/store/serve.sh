#!/usr/bin/env bash
# Serves the store folder on UO_STORE_URL in the foreground until Ctrl+C, printing each request.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
cd "$UO_ROOT"
exec "$UO_PYTHON" "$UO_TOOLS/asset_store/run.py" serve "$@"
