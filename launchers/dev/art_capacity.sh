#!/usr/bin/env bash
# Runs the synthetic art-capacity Labs contract or checks its .NET dependency (doctor).
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ "$#" -eq 0 ]; then set -- test; fi
"$UO_PYTHON" "$UO_TOOLS/art_capacity/run.py" "$@"
