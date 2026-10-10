#!/usr/bin/env bash
# Opens a Godot window that measures what adding one sprite to a texture atlas costs at each page size and prints the timings.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" --script res://dev/atlas_probe.gd "$@"
