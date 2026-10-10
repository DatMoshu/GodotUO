#!/usr/bin/env bash
# Plays a scripted session against the configured shard (log in, walk, open the backpack, speak), prints each check and exits non-zero if any fails.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
mkdir -p "$UO_BUILD/screenshots"
echo "[playtest] Driving a session against $UO_SHARD_HOST:$UO_SHARD_PORT"
if "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --play --input-probe \
    --screenshot-dir "$UO_BUILD/screenshots" "$@"
then
    echo "[playtest] OK"
else
    echo "[playtest] FAILED"
    exit 1
fi
