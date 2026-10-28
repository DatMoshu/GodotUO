#!/usr/bin/env bash
# Tests two GUO clients for movement visibility and pending atlas uploads on the local dev shard.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/mobile_sync/run.py" "$@"
