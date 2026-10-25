#!/usr/bin/env bash
# Checks or synchronizes the client version from version.json; pass check or sync.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/release/run.py" "$@"
