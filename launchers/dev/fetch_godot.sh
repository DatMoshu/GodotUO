#!/usr/bin/env bash
# ============================================================================
#  Restores the pinned Godot build into tools/godot (it is gitignored).
#  No-op if the pinned version is already present. Twin of fetch_godot.bat.
# ============================================================================
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
echo "[fetch] Extracting to $UO_TOOLS/godot"
unzip -q -o "$tmp/$asset" -d "$UO_TOOLS/godot"
chmod +x "$GODOT_EXE"
echo "[fetch] Done."
