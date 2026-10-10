#!/usr/bin/env bash
# Plays a long session against the shard (five minutes unless given seconds) and fails if frame time, object count or memory drift between its first and last stretch.
# args: <seconds>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
endure="${1:-300}"
echo "[endurance] Playing on for $endure seconds after the usual session"
if "$(dirname "$0")/playtest.sh" --endure "$endure"; then
    echo "[endurance] OK"
else
    echo "[endurance] FAILED"
    exit 1
fi
