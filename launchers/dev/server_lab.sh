#!/usr/bin/env bash
# The server compatibility lab: runs GUO's scripted cases against another UO server (ModernUO, ServUO, ...) and rewrites the wiki row; doctor checks the setup.
#   launchers/dev/server_lab.sh doctor
#   launchers/dev/server_lab.sh row modernuo     set up, start, seed, run the cases, stop, rewrite the wiki
# See tools/server_lab/README.md. Take the switchboard leases first (build:D, shard:PORT, godot:runtime).
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_ROOT/tools/server_lab/run.py" "$@"
