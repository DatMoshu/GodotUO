#!/usr/bin/env bash
# Photographs five fixed places on the dev shard into build/screenshots/sweep, one client run per spot, and fails if any spot fails.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
sweep="$UO_BUILD/screenshots/sweep"
mkdir -p "$sweep"
fail=0
shot() {
    echo "[sweep] $1 at $2 $3"
    if ! "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --play \
        --screenshot-dir "$sweep" \
        --screenshot-name "$1" \
        --shard-command "[go $2 $3"; then
        echo "[sweep] $1 FAILED"
        fail=1
    fi
}
shot minoc-town       2500 560
shot yew-forest        633 858
shot britain-coast    1497 1790
shot despise-mouth    5401 629
shot britain-street   1602 1591
echo "[sweep] Output: $sweep"
exit "$fail"
