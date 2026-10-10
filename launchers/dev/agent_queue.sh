#!/usr/bin/env bash
# Posts to, tails, replies on or lists the editor's agent request queue and prints the messages.
# args: post|tail|reply|list ...
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/agent_queue/run.py" "$@"
