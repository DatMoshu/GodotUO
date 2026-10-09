#!/usr/bin/env bash
# Exports, installs and starts GUO on the attached device, waits for the login gump, pulls a screenshot and the log into build/android and fails if the gump never appears.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/android/run.py" smoke "$@"
