#!/usr/bin/env bash
# Run the local dev shard (Ctrl-C to stop). Twin of run.bat.
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1

if [ ! -x "$UO_SHARD_DIST/ModernUO" ]; then
    echo "[shard] Not built yet. Run launchers/shard/build.sh first."
    exit 1
fi

"$UO_PYTHON" "$UO_TOOLS/modernuo/configure.py" || exit 1

echo "[shard] $UO_SHARD_NAME on $UO_SHARD_HOST:$UO_SHARD_PORT  (Ctrl-C to stop)"
cd "$UO_SHARD_DIST" && exec ./ModernUO "$@"
