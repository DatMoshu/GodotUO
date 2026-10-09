#!/usr/bin/env bash
# Runs the fast health check (engine, import, C# build, client data, offline load, editor add-on, launcher lint) and prints OK or FAILED.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
FAIL=0

echo "[smoke] 1/7 engine"
"$GODOT_CONSOLE" --version || FAIL=1

echo "[smoke] 2/7 project imports"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit || FAIL=1

echo "[smoke] 3/7 C# builds"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit || FAIL=1

echo "[smoke] 4/7 client data"
"$UO_PYTHON" "$UO_TOOLS/uodata/run.py" verify --data-dir "${UO_CLIENT_DATA:-}" --client-version "$UO_CLIENT_VERSION" --quiet || FAIL=1

# Offline mode exercises what the other steps cannot: the ported readers
# against the real install, and the resources compiled into the assembly.
echo "[smoke] 5/7 client offline load"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --offline || FAIL=1

echo "[smoke] 6/7 editor add-on (headless; tools/editor_smoke)"
"$UO_PYTHON" "$UO_TOOLS/editor_smoke/run.py" --no-build || FAIL=1

echo "[smoke] 7/7 launchers (tools/launcher_lint)"
"$UO_PYTHON" "$UO_TOOLS/launcher_lint/run.py" || FAIL=1

echo
if [ "$FAIL" = "1" ]; then echo "[smoke] FAILED"; exit 1; fi
echo "[smoke] OK"
