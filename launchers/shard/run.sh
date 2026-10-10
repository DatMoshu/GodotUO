#!/usr/bin/env bash
# Run the local dev shard (Ctrl-C to stop). Twin of run.bat.
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1

if [ ! -x "$UO_SHARD_DIST/ModernUO" ]; then
    echo "[shard] Not built yet. Run launchers/shard/build.sh first."
    exit 1
fi

"$UO_PYTHON" "$UO_TOOLS/modernuo/configure.py" || exit 1

# configure.py may just have generated the passwords: resolve again so the
# shard's headless boot (patch 0001) sets the owner and GM accounts to them.
eval "$("$UO_PYTHON" "$UO_ROOT/tools/shellenv/run.py")" || exit 1
# The editor bridge, when this shard loads it, opens its admin channel only
# with this token (ADR-0035); the Admin tab sends the same one.
if [ -n "${UO_BRIDGE_ADMIN_TOKEN:-}" ]; then export GUO_BRIDGE_ADMIN_TOKEN="$UO_BRIDGE_ADMIN_TOKEN"; fi

echo "[shard] $UO_SHARD_NAME on $UO_SHARD_BIND:$UO_SHARD_PORT  (Ctrl-C to stop)"
cd "$UO_SHARD_DIST" && exec ./ModernUO "$@"
