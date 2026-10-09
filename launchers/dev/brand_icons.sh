#!/usr/bin/env bash
# Rebuilds every app icon and the splash from design/brand/guo-sigil.png and lists the files it wrote.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/brand/run.py" "$@"
