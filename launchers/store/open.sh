#!/usr/bin/env bash
# Opens the local GUO Asset Store page (UO_STORE_URL) in your browser, which detaches (start) so this returns at once.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
opener=xdg-open; [ "$(uname -s)" = "Darwin" ] && opener=open
"$opener" "$UO_STORE_URL" >/dev/null 2>&1 &
