#!/usr/bin/env bash
# Verifies pinned ComfyUI nodes and model hashes offline; --validate also checks server node types.
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_ROOT/tools/comfy/setup_studio.py" --check "$@"
