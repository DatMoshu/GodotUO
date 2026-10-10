#!/usr/bin/env bash
# Lists what a Linux export needs on this machine and what the Steam Deck has over ssh, with the fix for each miss.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" doctor "$@"
