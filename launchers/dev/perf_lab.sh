#!/usr/bin/env bash
# Measures GUO's frame rate on this machine at 1920x1080 and 2560x1440 (zoom 2.5) and writes a report to share in Discord; needs the dev shard running.
# args: [--preset 1080p^|1440p-far] [--size W,H --zoom Z] [--scene NAME] [--doctor] [--as-built]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/perf_lab/run.py" "$@"
