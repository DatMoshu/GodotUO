#!/usr/bin/env bash
# Runs the WebSocket-to-TCP bridge between the browser client and the shard in the foreground until Ctrl+C, or tests a login through it.
# args: test [--fake]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then
    exec "$UO_PYTHON" "$UO_TOOLS/ws_bridge/run.py" serve
fi
exec "$UO_PYTHON" "$UO_TOOLS/ws_bridge/run.py" "$@"
