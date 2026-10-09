#!/usr/bin/env bash
# Exports and runs the portrait probe on the attached device, photographs each portrait and landscape hold and writes the measurements to build/android/portrait.
# args: --args "<client flags>"
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" portrait_probe "$@"
