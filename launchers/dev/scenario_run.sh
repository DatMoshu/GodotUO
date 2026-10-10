#!/usr/bin/env bash
# Runs a scripted scenario with the AI or human driver and records it under build/runs, or lists and validates scenarios (lists them when given nothing).
# args: <scenario id> [--driver ai|human] | list | validate <id>
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- list; fi
exec "$UO_PYTHON" "$UO_TOOLS/scenario_run/run.py" "$@"
