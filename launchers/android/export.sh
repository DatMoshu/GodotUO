#!/usr/bin/env bash
# Exports an APK headless into build/android: GUO-debug.apk, or with --release GUO-release.apk signed with your own key.
# args: --args "<client flags to bake in>" --release
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" export "$@"
