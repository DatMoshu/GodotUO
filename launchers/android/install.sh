#!/usr/bin/env bash
# Installs build/android/GUO-debug.apk onto the attached Android device with adb and reports the result.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" install "$@"
