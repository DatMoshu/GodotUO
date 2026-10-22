#!/usr/bin/env bash
# Runs the benchmark tour (GUO plain, defaults and UO+ max against ClassicUO at the same spots and zooms) and writes tour.md, tour.json and tour.png under build/perf_tour/<label>.
# args: [--label <name>] [--preset gate|community|far] [--mode plain|defaults|uoplus-max] [--scene <name>] [--no-cuo] [--as-built] | --report <dir>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/perf_tour/run.py" "$@"
