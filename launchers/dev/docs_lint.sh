#!/usr/bin/env bash
# Checks every local Markdown link and heading anchor in the repository and prints each broken one.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/docs_lint/run.py" "$@"
