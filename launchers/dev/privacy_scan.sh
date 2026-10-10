#!/usr/bin/env bash
# Scans the tracked files for LAN addresses, personal emails, home folders and machine paths and prints each hit.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/privacy_scan/run.py" "$@"
