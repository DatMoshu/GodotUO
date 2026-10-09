#!/usr/bin/env bash
# Logs ClassicUO and GUO into the dev shard at the same five spots and writes one comparison sheet per spot; the ClassicUO half types into its window, so it needs --allow-foreground and a desktop left alone.
# args: --only guo|cuo | --place <name> | --allow-foreground
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/ab_compare/run.py" "$@"
