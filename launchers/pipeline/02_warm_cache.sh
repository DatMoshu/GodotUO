#!/usr/bin/env bash
# Pre-decodes UO art into the runtime cache headless so the first visit to a place does not hitch; needs step 01's manifest.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ ! -f "$UO_BUILD/client_manifest.json" ]; then
    echo "[02] No manifest. Run pipeline/01_verify_client_data.sh first."
    exit 1
fi
exec "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --warm-cache "$@"
