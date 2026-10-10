#!/usr/bin/env bash
# Starts the installed client on the attached device and streams its logcat here until Ctrl+C; the app keeps running afterwards.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" run "$@"
