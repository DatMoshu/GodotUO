#!/usr/bin/env bash
# Clones or updates the read-only ClassicUO reference under sources and prints the upstream commits since the last sync.
# args: --pin | --at-pin
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/sync_upstream/run.py" --root "$UO_ROOT" "$@"
