#!/usr/bin/env bash
# Builds, stages and proves towns on a generated map with tools/mapgen_districts; prints its usage when given nothing.
# args: town|build|stage|prove ...
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/mapgen_districts/run.py" "$@"
