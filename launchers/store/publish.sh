#!/usr/bin/env bash
# Packs and publishes the local asset packs into the store folder (UO_STORE_DIR) and prints what it published.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
cd "$UO_ROOT"
exec "$UO_PYTHON" "$UO_TOOLS/asset_store/run.py" publish "$@"
