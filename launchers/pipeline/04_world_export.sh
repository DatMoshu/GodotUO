#!/usr/bin/env bash
# Exports the editor's world project into patched map and statics files under <project>/export, then reads them back to check them.
# args: --project <dir>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
"$UO_PYTHON" "$UO_TOOLS/world/run.py" export "$@"
exec "$UO_PYTHON" "$UO_TOOLS/world/run.py" verify "$@"
