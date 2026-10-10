#!/usr/bin/env bash
# Exports the Linux x86_64 build headless into build/steamdeck and writes build/steamdeck/export.log.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/steamdeck/run.py" export "$@"
