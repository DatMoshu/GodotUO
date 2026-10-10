#!/usr/bin/env bash
# Downloads the pinned Godot build into tools/godot (or UO_GODOT_HOME) unless it is already there, and prints each step.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh"

if [ -x "$GODOT_EXE" ]; then
    echo "[fetch] Godot $GODOT_VERSION already present."
    exit 0
fi
case "$GODOT_FLAVOR" in
    mono_linux_*) asset="Godot_v${GODOT_VERSION}_${GODOT_FLAVOR}.zip" ;;
    *) echo "[fetch] FATAL: no download rule for flavor $GODOT_FLAVOR"; exit 1 ;;
esac
url="https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}/${asset}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
echo "[fetch] Downloading $url"
curl -fL --progress-bar -o "$tmp/$asset" "$url"
echo "[fetch] Extracting to $UO_GODOT_HOME"
unzip -q -o "$tmp/$asset" -d "$UO_GODOT_HOME"
chmod +x "$GODOT_EXE"
echo "[fetch] Done."
