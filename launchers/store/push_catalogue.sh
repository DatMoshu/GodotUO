#!/usr/bin/env bash
# Signs a catalogue index over a GitHub Pages branch and pushes it, fast-forward only (needs UO_STORE_SIGNING_KEY).
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
cd "$UO_ROOT"
exec "$UO_PYTHON" "$UO_TOOLS/asset_store/run.py" push-catalogue "$@"
