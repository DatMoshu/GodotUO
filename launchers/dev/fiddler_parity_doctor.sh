#!/usr/bin/env bash
# Checks or builds the independent static-art reader used to verify Fiddler-parity staged output.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
python "$UO_TOOLS/fiddler_parity/run.py" doctor "$@"
