#!/usr/bin/env bash
# Checks what L4 map paging needs: dotnet, the engine-free paging tests, the page manifest in GUO_LABS_PAGING and a Labs build; --probe also runs the headless loader probe.
# args: [--probe] [--fixture <dir>]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
FAIL=0
TESTS="$UO_ROOT/tools/paging_tests/PagingTests.csproj"
command -v dotnet >/dev/null 2>&1 || { echo "[paging-doctor] dotnet is missing: install the .NET 8 SDK."; exit 1; }
if [ "${1:-}" = "--fixture" ]; then
    [ -n "${2:-}" ] || { echo "[paging-doctor] --fixture needs a folder outside the checkout, e.g. under UO_BUILD_DIR."; exit 2; }
    exec dotnet run --project "$TESTS" --artifacts-path "$UO_BUILD_DIR/paging-tests" -- --fixture "$2"
fi
echo "[paging-doctor] 1/4 paging core tests (no engine, no client data)"
dotnet run --project "$TESTS" --artifacts-path "$UO_BUILD_DIR/paging-tests" || FAIL=1
echo "[paging-doctor] 2/4 page manifest"
if [ -n "${GUO_LABS_PAGING:-}" ]; then
    dotnet run --project "$TESTS" --artifacts-path "$UO_BUILD_DIR/paging-tests" -- --validate "$GUO_LABS_PAGING" || FAIL=1
else
    echo "[paging-doctor] GUO_LABS_PAGING is not set: paging stays off. Set it to a page manifest; --fixture <dir> writes a synthetic one."
fi
echo "[paging-doctor] 3/4 Labs build (Debug, GuoLabs=true)"
dotnet build "$UO_GODOT_PROJECT/GUO.csproj" -c Debug -p:GuoLabs=true -nologo -v q || FAIL=1
if [ "${1:-}" = "--probe" ]; then
    echo "[paging-doctor] 4/4 headless loader probe against UO_CLIENT_DATA"
    "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit-after 120 res://src/Labs/Paging/PagingProbe.tscn || FAIL=1
else
    echo "[paging-doctor] 4/4 loader probe skipped; add --probe to run it (starts headless Godot)"
fi
if [ "$FAIL" = "1" ]; then echo "[paging-doctor] FAILED: see godot/GUO/src/Labs/Paging/README.md"; exit 1; fi
echo "[paging-doctor] OK"
