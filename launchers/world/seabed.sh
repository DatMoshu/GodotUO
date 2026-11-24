#!/usr/bin/env bash
# Plans the seabed and shallows for a whole facet with tools/seabed and stages it outside the install.
# args: generate|stats [--facet N] [--seed N] [--out DIR]
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ $# -eq 0 ]; then set -- --help; fi
exec "$UO_PYTHON" "$UO_TOOLS/seabed/run.py" "$@"
