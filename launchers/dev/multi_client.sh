#!/usr/bin/env bash
# Runs four scripted clients against the dev shard, tiled 2x2 on screen, and writes their logs, frames and a contact sheet to build/multi_client.
# args: --only <lanes> | --list | --sound
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/multi_client/run.py" "$@"
