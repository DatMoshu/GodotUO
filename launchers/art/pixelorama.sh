#!/usr/bin/env bash
# Opens a PNG in Pixelorama with the GUO tools extension (UO hue palettes, size checks, Save back to GUO); Pixelorama's window opens and this waits for it.
# args: <file.png> [--sidecar <file.json>]
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/pixelorama/run.py" open "$@"
