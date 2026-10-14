#!/usr/bin/env bash
# Proves the -moge gump in the running game: probe login, -moge, select a static, Generate, screenshots, quits.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
: "${GUO_MOGE_MODEL:=Ruicheng/moge-2-vits-normal}"
export GUO_MOGE_MODEL
mkdir -p "$UO_BUILD/screenshots/moge"
"$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --moge-shot --autologin --screenshot-dir "$UO_BUILD/screenshots/moge" "$@"
echo "[moge-shot] Output: $UO_BUILD/screenshots/moge"
