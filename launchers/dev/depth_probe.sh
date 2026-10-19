#!/usr/bin/env bash
# Builds the C#, then draws the same quads through the canvas and the world depth pass and prints whether pixels and depth order agree (PERF-GPU step 1).
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
"$(dirname "$0")/build.sh"
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" "$@" -- --depth-probe
