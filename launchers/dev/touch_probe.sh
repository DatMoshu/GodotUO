#!/usr/bin/env bash
# Runs the client with the touch layer and a synthetic finger against the shard and prints a pass or fail line per gesture.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
mkdir -p "$UO_BUILD/screenshots"
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --play --touch-probe --screenshot-dir "$UO_BUILD/screenshots" --screenshot-name touch_probe "$@"
