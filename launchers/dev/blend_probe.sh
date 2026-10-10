#!/usr/bin/env bash
# Opens a Godot window that checks a canvas item can read what was drawn under it (the effect blend modes) and prints the answer.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" --script res://dev/blend_probe.gd "$@"
