#!/usr/bin/env bash
# Photographs the Steam Deck's screen over ssh into build/steamdeck/screenshot.png (Desktop mode only).
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" screenshot "$@"
