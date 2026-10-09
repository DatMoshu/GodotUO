#!/usr/bin/env bash
# Opens the project's generated C# solution in your default IDE, which detaches (start) so this returns at once.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ ! -f "$UO_GODOT_PROJECT/GUO.sln" ]; then
    echo "[sln] No solution yet. Build it first: launchers/dev/build.sh"
    exit 1
fi
opener=xdg-open; [ "$(uname -s)" = "Darwin" ] && opener=open
"$opener" "$UO_GODOT_PROJECT/GUO.sln" >/dev/null 2>&1 &
