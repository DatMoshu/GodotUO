#!/usr/bin/env bash
# Build the C# assemblies only. Twin of build.bat.
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[build] Building C# assemblies for $UO_GODOT_PROJECT"
exec "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit
