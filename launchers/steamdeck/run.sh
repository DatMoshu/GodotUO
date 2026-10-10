#!/usr/bin/env bash
# Starts GUO in the Steam Deck's Desktop-mode session over ssh; output goes to guo.log on the Deck.
# args: --wait <seconds> | --args "<client flags>"
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" run "$@"
