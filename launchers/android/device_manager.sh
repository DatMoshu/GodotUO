#!/usr/bin/env bash
# Opens the Device Test Manager window to pick a device profile, apply it to an emulator or attached device, install and run GUO and collect screenshots; it stays open until you close it.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ -n "${UO_ANDROID_JDK:-}" ]; then export JAVA_HOME="$UO_ANDROID_JDK"; fi
exec "$UO_PYTHON" "$UO_TOOLS/android/device_manager.py" "$@"
