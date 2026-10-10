#!/usr/bin/env bash
# Measures frame time in fixed scenes against the shard and writes build/perf/perf_<label>.md and .json, or compares two labels.
# args: --label <name> [--args "<client flags>"] | --compare <a> <b>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/perf_probe/run.py" "$@"
