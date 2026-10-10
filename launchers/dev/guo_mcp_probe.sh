#!/usr/bin/env bash
# Self-tests the client automation MCP bridge and prints each check; --client also starts a client to drive.
# args: --headed | --client
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/guo_mcp/run.py" probe "$@"
