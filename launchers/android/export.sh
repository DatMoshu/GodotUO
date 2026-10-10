#!/usr/bin/env bash
# Exports a debug APK headless into build/android/GUO-debug.apk and prints the export log as it goes.
# args: --args "<client flags to bake in>"
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" export "$@"
