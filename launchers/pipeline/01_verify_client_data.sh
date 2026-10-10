#!/usr/bin/env bash
# Checks every required UO data file is present, detects the client version and writes build/client_manifest.json; never writes into the install.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/uodata/run.py" verify \
    --data-dir "${UO_CLIENT_DATA:-}" \
    --client-version "$UO_CLIENT_VERSION" \
    --out "$UO_BUILD/client_manifest.json" "$@"
