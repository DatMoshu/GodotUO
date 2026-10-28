#!/usr/bin/env bash
# Checks dependencies and output configuration for the Labs mobile frame inspector.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/mobile_sync/doctor.py" "$@"
