#!/usr/bin/env bash
# Renders the procedural background loops (or the screensavers) into the project's assets folder and prints each video it wrote.
# args: --only <themes> | --set builtin|screensavers|store | --size WxH
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/bg_videos/run.py" "$@"
