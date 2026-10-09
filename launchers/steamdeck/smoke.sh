#!/usr/bin/env bash
# Exports, pushes and starts GUO on the Steam Deck, waits for the login gump, photographs it into build/steamdeck/smoke.png and fails if it never appears.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" smoke "$@"
