#!/usr/bin/env bash
# Runs the new-player gate: a fresh account creates a character, plays and comes back; exit 0 only if every link passes (needs a shard).
# args: [--list] [--only create|return] [--account NAME] [scenario runner options]
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/newplayer_gate/run.py" "$@"
