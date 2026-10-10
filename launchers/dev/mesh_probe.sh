#!/usr/bin/env bash
# Opens a Godot window that checks a canvas mesh can carry a per-vertex value (ARRAY_CUSTOM0) for land lighting and prints the answer.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" --script res://dev/mesh_probe.gd "$@"
