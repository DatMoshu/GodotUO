#!/usr/bin/env bash
# Opens a Godot window that checks Godot can play UO's sound effects and music directly and prints PASS, FAIL or SKIP per stage.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" --script res://dev/audio_probe.gd "$@"
