#!/usr/bin/env bash
# Deletes the project's .godot folder and re-imports headless, rebuilding Godot's class and script cache; prints each step.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[cache] Removing $UO_GODOT_PROJECT/.godot"
rm -rf -- "$UO_GODOT_PROJECT/.godot"
echo "[cache] Re-importing..."
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --import --quit
echo "[cache] Done."
