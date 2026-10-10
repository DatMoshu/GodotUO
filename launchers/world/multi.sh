#!/usr/bin/env bash
# Mines, builds, validates and writes new multis (houses to castles) with tools/multi; prints its usage when given nothing.
# args: mine|sheets|build|write|prove ...
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/multi/run.py" "$@"
