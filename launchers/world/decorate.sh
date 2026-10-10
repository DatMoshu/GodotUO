#!/usr/bin/env bash
# Furnishes generated multis from UO's own interiors with tools/decorate; prints its usage when given nothing.
# args: mine|stats|decorate|preview|demo ...
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/decorate/run.py" "$@"
