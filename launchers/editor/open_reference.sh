#!/usr/bin/env bash
# Opens the read-only upstream ClassicUO solution in your default IDE, which detaches (start) so this returns at once.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ ! -f "$UO_SOURCES/ClassicUO/ClassicUO.sln" ]; then
    echo "[ref] Upstream missing. Run: launchers/dev/sync_upstream.sh"
    exit 1
fi
echo "[ref] REFERENCE ONLY - do not edit files under sources/"
opener=xdg-open; [ "$(uname -s)" = "Darwin" ] && opener=open
"$opener" "$UO_SOURCES/ClassicUO/ClassicUO.sln" >/dev/null 2>&1 &
