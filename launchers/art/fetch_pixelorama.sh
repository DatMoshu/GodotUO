#!/usr/bin/env bash
# Downloads Pixelorama's pinned source and release build into tools/pixelorama (gitignored) and prints what it fetched.
# args: --source | --binary
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/pixelorama/run.py" fetch "$@"
