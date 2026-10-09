#!/usr/bin/env bash
# Checks the editor's live tier end to end with two editors and a client on the private shard and prints the result.
# args: --no-export | --facet 0|1 | --windowed
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/editor_live/run.py" "$@"
