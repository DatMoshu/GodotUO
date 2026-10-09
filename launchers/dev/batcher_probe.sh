#!/usr/bin/env bash
# Builds the C#, then draws through the real UltimaBatcher2D in a Godot window and prints whether the C# and shader hue packing agree.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
"$(dirname "$0")/build.sh"
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --batcher-probe "$@"
