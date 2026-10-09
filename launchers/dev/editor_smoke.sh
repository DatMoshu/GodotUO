#!/usr/bin/env bash
# Opens the Godot editor on the project, lets the GUO add-on check its docks against the real client install, and writes a report and screenshots to build/editor_smoke.
# args: --headless | --reload
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/editor_smoke/run.py" "$@"
