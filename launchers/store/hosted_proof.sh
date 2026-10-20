#!/usr/bin/env bash
# Proves a hosted catalogue on this computer: signs and pushes to a local Pages branch, serves it, and installs from it with the client's C#.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
cd "$UO_ROOT"
exec "$UO_PYTHON" "$UO_TOOLS/asset_store/hosted_proof.py" "$@"
