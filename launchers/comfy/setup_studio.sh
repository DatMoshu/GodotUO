#!/usr/bin/env bash
# Checks (or installs) the ComfyUI models and custom nodes GUO's art workflows need, then validates them against the server.
# args: [--check] [--comfy-dir DIR] [--url URL]
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_ROOT/tools/comfy/setup_studio.py" "$@"
