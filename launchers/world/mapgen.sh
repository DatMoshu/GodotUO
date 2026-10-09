#!/usr/bin/env bash
# Generates a UO map procedurally with tools/mapgen and validates it; prints its usage when given nothing.
# args: prepare|schema|run ...
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/mapgen/run.py" "$@"
