#!/usr/bin/env bash
# ============================================================================
#  Fast health check: engine present, project imports, C# builds,
#  client data readable, and the editor add-on (headless, about 20 s).
#  Run before every commit. Twin of smoke.bat.
# ============================================================================
. "$(dirname "$0")/../_shared/common.sh" || exit 1
FAIL=0

echo "[smoke] 1/6 engine"
"$GODOT_CONSOLE" --version || FAIL=1

echo "[smoke] 2/6 project imports"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit || FAIL=1

echo "[smoke] 3/6 C# builds"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit || FAIL=1

echo "[smoke] 4/6 client data"
"$UO_PYTHON" "$UO_TOOLS/uodata/run.py" verify --data-dir "${UO_CLIENT_DATA:-}" --client-version "$UO_CLIENT_VERSION" --quiet || FAIL=1

# Offline mode exercises what the other steps cannot: the ported readers
# against the real install, and the resources compiled into the assembly.
echo "[smoke] 5/6 client offline load"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --offline || FAIL=1

echo "[smoke] 6/6 editor add-on (headless; tools/editor_smoke)"
"$UO_PYTHON" "$UO_TOOLS/editor_smoke/run.py" --no-build || FAIL=1

echo
if [ "$FAIL" = "1" ]; then echo "[smoke] FAILED"; exit 1; fi
echo "[smoke] OK"
