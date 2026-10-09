#!/usr/bin/env bash
# Builds the project and checks Gump Studio in a headless editor, printing pass or fail per check.
# args: --reload | --windowed
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/gump_studio_smoke/run.py" "$@"
