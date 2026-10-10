#!/usr/bin/env bash
# Measures how far ported files have drifted from their upstream originals and prints a report.
# args: --strict
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/port_drift/run.py" "$@"
