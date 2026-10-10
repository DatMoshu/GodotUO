#!/usr/bin/env bash
# Lists what an Android export needs on this machine (JDK 17, SDK, templates, keystore, a device) and prints the fix for each missing piece.
# args: --publish
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" doctor "$@"
