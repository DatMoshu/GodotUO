#!/usr/bin/env bash
# Lists what a C# web export needs (the community Godot build, its templates and SDK) and what this machine has, with a MISS line for each gap.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/web/run.py" doctor "$@"
