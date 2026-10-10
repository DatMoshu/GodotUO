#!/usr/bin/env bash
# Proves ExtractDecal headless: two PNGs in, decal out, stats printed.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --extract-test "$@"
