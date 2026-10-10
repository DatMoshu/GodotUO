#!/usr/bin/env bash
# Runs every Python test folder under tools\ in one pooled pytest run (pytest.ini, conftest.py).
# Extra arguments go to pytest, e.g. pytest_all.sh -x or pytest_all.sh tools/guo
set -euo pipefail
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
cd "$UO_ROOT" || exit 1
echo "[pytest_all] Running the pooled Python tests in $UO_ROOT"
if "$UO_PYTHON" -m pytest "$@"; then
    echo "[pytest_all] OK"
else
    rc=$?
    echo "[pytest_all] FAILED"
    exit $rc
fi
