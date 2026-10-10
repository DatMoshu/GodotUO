#!/usr/bin/env bash
# Runs the client with its loopback automation MCP on GUO_MCP_PORT (token in GUO_MCP_TOKEN) and stays in the foreground until the client exits.
# args: --headless
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
if [ -z "${GUO_MCP_PORT:-}" ]; then
    echo "[mcp] Set GUO_MCP_PORT to an unused local port, e.g. 18670."
    exit 1
fi
if [ -z "${GUO_MCP_TOKEN:-}" ]; then
    echo "[mcp] Set GUO_MCP_TOKEN to a random secret of at least 32 characters."
    exit 1
fi
display=()
if [ "${1:-}" = "--headless" ]; then display=(--headless); shift; fi
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" ${display[@]+"${display[@]}"} -- --no-focus --silent "$@"
