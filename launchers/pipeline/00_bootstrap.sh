#!/usr/bin/env bash
# Sets up a fresh clone: fetches the pinned Godot and the upstream ClassicUO reference, then verifies the UO client data.
set -euo pipefail
here="$(dirname "$0")"
echo "[00] Fetching pinned Godot (skipped if already present)..."
"$here/../dev/fetch_godot.sh"
. "$here/../_shared/common.sh"
echo "[00] Fetching upstream ClassicUO at the reviewed pin..."
"$UO_PYTHON" "$UO_TOOLS/sync_upstream/run.py" --root "$UO_ROOT" --at-pin
echo "[00] Verifying client data..."
"$here/01_verify_client_data.sh"
echo
echo "[00] Bootstrap complete. Next: launchers/game/play.sh"
