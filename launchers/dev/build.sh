#!/usr/bin/env bash
# Builds the Godot project's C# assemblies headless, without opening the editor, and prints the compiler output.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
echo "[build] Building C# assemblies for $UO_GODOT_PROJECT"
# UO_BUILD_TIMEOUT (seconds, _shared/config.sh) bounds the engine run: a
# headless --quit that never quits is killed and reported as exit 124, so
# automation never hangs forever. A healthy build keeps its own exit code.
exec timeout -k 30s "${UO_BUILD_TIMEOUT:-600}" "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit
