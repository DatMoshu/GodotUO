#!/usr/bin/env bash
# Launches the injected ClassicUO unfocused with its automation MCP and drives it through login, a [go and a screenshot (proof), or runs the tool self-test (selftest); needs the shard for proof.
# args: proof [--build] [--run-dir DIR] [--size W,H] | selftest
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/cuo_automation/run.py" "$@"
