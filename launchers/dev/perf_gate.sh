#!/usr/bin/env bash
# Runs the real performance probe against a machine-local baseline; --record-baseline records it explicitly and --doctor checks prerequisites.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/perf_probe/local_gate.py" "$@"
