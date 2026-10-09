#!/usr/bin/env bash
# Walks every upstream ClassicUO file, classifies how far it is ported and rewrites docs/port_status.md.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/port_audit/run.py" \
    --upstream "$UO_SOURCES/ClassicUO" \
    --port "$UO_GODOT_PROJECT" \
    --out "$UO_DOCS/port_status.md" "$@"
