#!/usr/bin/env bash
# Runs the frozen competition GPU benchmark (Labs CompetitionBench probe): timed or capture, interleaved, scored against the frozen common baseline.
# args: --label L --out EVIDENCE_DIR [--builds A=path ...] [--variants ...] [--mode timed|capture] [--baseline DIR]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/competition_bench/run.py" "$@"
