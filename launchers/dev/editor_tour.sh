#!/usr/bin/env bash
# Records a captioned tour of every GUO editor feature as an MP4 under build/editor_tour; an editor window opens without taking focus.
# args: --no-live
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/editor_tour/run.py" "$@"
