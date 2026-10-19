#!/usr/bin/env bash
# Checks what the perf lab needs (.NET, Godot, the UO client data, a running shard) and says how to fix what is missing; measures nothing.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/perf_lab/run.py" --doctor "$@"
