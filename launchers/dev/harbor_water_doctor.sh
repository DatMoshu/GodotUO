#!/usr/bin/env bash
# Checks the optional Labs Rust water solver and its callable entry point.
set -e
source "$(dirname "$0")/../_shared/common.sh"
"$UO_PYTHON" "$UO_TOOLS/harbor_water/doctor.py"
