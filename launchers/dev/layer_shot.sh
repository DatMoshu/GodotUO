#!/usr/bin/env bash
# Photographs a terrain layer: probe login, shard [go, two frames saved, quits.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
mkdir -p "$UO_BUILD/screenshots/layer_shot"
"$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --layer-shot --autologin --screenshot-dir "$UO_BUILD/screenshots/layer_shot" "$@"
echo "[layer-shot] Output: $UO_BUILD/screenshots/layer_shot"
