#!/usr/bin/env bash
# Serves build/web on UO_WEB_PORT with the cross-origin isolation headers in the foreground until Ctrl+C.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/web/run.py" serve "$@"
