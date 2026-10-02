#!/usr/bin/env bash
# Check the UO install. Twin of 01_verify_client_data.bat.
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/uodata/run.py" verify \
    --data-dir "${UO_CLIENT_DATA:-}" \
    --client-version "$UO_CLIENT_VERSION" \
    --out "$UO_BUILD/client_manifest.json" "$@"
