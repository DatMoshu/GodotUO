#!/usr/bin/env bash
# Prints what of Pixelorama is fetched and installed and where the art exchange folder is.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/pixelorama/run.py" status
