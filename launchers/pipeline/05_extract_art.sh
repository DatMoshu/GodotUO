#!/usr/bin/env bash
# Pipeline step 05 (optional): extracts your install's art into a local, gitignored art set and verifies every image against the pages.
#   05_extract_art.sh                     export everything, then verify
#   05_extract_art.sh --what art,gumps    only those classes
#   05_extract_art.sh --from DIR          another client folder
# See docs/art_extract.md and tools/art_extract/README.md.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
"$UO_PYTHON" "$UO_TOOLS/art_extract/run.py" export "$@"
exec "$UO_PYTHON" "$UO_TOOLS/art_extract/run.py" verify "$@"
