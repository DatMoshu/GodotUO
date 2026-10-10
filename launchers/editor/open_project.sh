#!/usr/bin/env bash
# Builds the C#, then opens the GUO project in the pinned Godot editor, which detaches (start) so this returns at once.
# args: -- <Godot flags>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[editor] Building C# for $UO_GODOT_PROJECT"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit \
    || echo "[editor] C# build failed; the UO docks will not load. See the output above."
echo "[editor] Opening $UO_GODOT_PROJECT in Godot $GODOT_VERSION"
nohup "$GODOT_EXE" --editor --path "$UO_GODOT_PROJECT" "$@" >/dev/null 2>&1 &
