#!/usr/bin/env bash
# Forces a full rebuild of godot/GUO/GUO.csproj and prints every C# error grouped by the missing symbol.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/port_errors/run.py" "$@"
