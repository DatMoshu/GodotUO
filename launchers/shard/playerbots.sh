#!/usr/bin/env bash
# Sets up, builds, runs or checks the optional PlayerBots ModernUO shard on its own port; run stays in the foreground until Ctrl+C.
# args: setup|build|run|play|populate|smoke|status
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/playerbots/run.py" "$@"
