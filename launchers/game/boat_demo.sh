#!/usr/bin/env bash
# Dev shard only: logs in on a staff account, puts a small boat on the water at the Haven docks with the live Water panel open, and leaves the game to you.
# args: [--account NAME] [client flags]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
acct=guoeffects
if [ "${1:-}" = "--account" ]; then acct="$2"; shift 2; fi
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --boat-demo --autologin --account "$acct" "$@"
