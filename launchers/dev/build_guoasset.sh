#!/usr/bin/env bash
# Builds the guoasset parity-reference MCP server into build/guoasset with the .NET SDK and prints where the dll landed.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ ! -d "$UO_SOURCES/ClassicUO/src/ClassicUO.Assets" ]; then
    echo "[guoasset] FATAL: no upstream checkout at \"$UO_SOURCES/ClassicUO\""
    echo "[guoasset] Fetch it with: launchers/dev/sync_upstream.sh"
    exit 1
fi
echo "[guoasset] Building tools/guoasset -> build/guoasset"
dotnet build "$UO_TOOLS/guoasset/GUO.AssetMcp.csproj" -c Release --artifacts-path "$UO_BUILD/guoasset" -v q --nologo
echo "[guoasset] OK: $UO_BUILD/guoasset/bin/GUO.AssetMcp/release/guoasset.dll"
