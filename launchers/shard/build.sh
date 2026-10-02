#!/usr/bin/env bash
# Publish the ModernUO dev shard for this OS. Twin of build.bat (.NET 10 SDK).
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1

if [ ! -f "$UO_SHARD_SRC/publish.sh" ]; then
    echo "[shard] Not cloned yet. Run launchers/shard/fetch.sh first."
    exit 1
fi

os=linux; [ "$(uname -s)" = "Darwin" ] && os=osx
arch=x64; case "$(uname -m)" in aarch64|arm64) arch=arm64 ;; esac
(cd "$UO_SHARD_SRC" && bash ./publish.sh release "$os" "$arch") || exit $?
echo "[shard] Built: $UO_SHARD_DIST"
