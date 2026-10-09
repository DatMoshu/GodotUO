#!/usr/bin/env bash
# Exports the web build headless into build/web and writes the export log to build/web/export.log.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/web/run.py" export "$@"
