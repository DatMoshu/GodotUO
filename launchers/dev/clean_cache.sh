#!/usr/bin/env bash
# Deletes the runtime decode cache (UO_CACHE_DIR) so the client rebuilds it on demand, and prints the folder it cleared.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[clean] Clearing $UO_CACHE_DIR"
rm -rf -- "$UO_CACHE_DIR"
mkdir -p -- "$UO_CACHE_DIR"
echo "[clean] Done."
