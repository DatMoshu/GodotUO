#!/usr/bin/env bash
# Builds GUO and runs the profile migration ladder headless for the desktop, mobile and web profiles, printing each result.
# args: --no-build
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/profile_migrations/run.py" "$@"
