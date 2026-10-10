#!/usr/bin/env bash
# Starts the client, captures one frame into build/screenshots and prints where it went.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
mkdir -p "$UO_BUILD/screenshots"
"$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --screenshot --screenshot-dir "$UO_BUILD/screenshots" "$@"
echo "[shot] Output: $UO_BUILD/screenshots"
