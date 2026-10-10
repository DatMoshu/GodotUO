#!/usr/bin/env bash
# Opens a Godot window that measures how many bits of the hue vector survive a canvas item's per-quad modulate and prints the result.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" --script res://dev/hue_probe.gd "$@"
