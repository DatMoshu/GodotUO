#!/usr/bin/env bash
# Exports, installs and logs in a build on the attached dual-screen device, photographs both displays into build/android and fails if the second screen never comes up.
# args: --args "<client flags>"
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" dual_probe "$@"
