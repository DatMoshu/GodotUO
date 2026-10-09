#!/usr/bin/env bash
# Unpacks UO art, gumps and animations to PNG plus JSON and packs edited folders back with tools/uopack; prints its usage when given nothing.
# args: unpack|pack|roundtrip|selftest ...
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/uopack/run.py" "$@"
