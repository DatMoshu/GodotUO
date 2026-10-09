#!/usr/bin/env bash
# Logs a GM into the dev shard, taps each touch-bar macro against spawned fixtures and prints a pass or fail line per macro.
# args: --account <name> --password <pass>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
mkdir -p "$UO_BUILD/screenshots"
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --play --macro-probe --screenshot-dir "$UO_BUILD/screenshots" --screenshot-name macro_probe "$@"
