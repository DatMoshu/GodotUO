#!/usr/bin/env bash
# Sets up or checks the editor's named local servers and clients and prints what is missing (runs doctor when given nothing).
# args: init|clients|doctor
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- doctor; fi
exec "$UO_PYTHON" "$UO_TOOLS/server_manager/run.py" "$@"
