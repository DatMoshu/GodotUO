#!/usr/bin/env bash
# Builds the Godot project's C# assemblies headless, without opening the editor, and prints the compiler output.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[build] Building C# assemblies for $UO_GODOT_PROJECT"
exec "$UO_PYTHON" "$UO_TOOLS/guo/build_guard.py" --timeout "$UO_BUILD_TIMEOUT" -- "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit
