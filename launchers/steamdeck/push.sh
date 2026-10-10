#!/usr/bin/env bash
# Copies build/steamdeck onto the Steam Deck over ssh and writes guo.sh there; never pushes the UO client data.
# args: --host <shard LAN address>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" push "$@"
