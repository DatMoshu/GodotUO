#!/usr/bin/env bash
# Makes the catalogue's signing key file (never overwriting one) and prints the public key and fingerprint to share.
# args: <path to the key file>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
key_file="${1:-${UO_STORE_SIGNING_KEY:-}}"
if [ -z "$key_file" ]; then
    echo "Give the key file's path, e.g.  keygen.sh ~/backups/catalogue/guo-official.key"
    echo "or set UO_STORE_SIGNING_KEY in launchers/_shared/config.local.sh."
    exit 1
fi
if [ -e "$key_file" ]; then
    echo "$key_file already exists. Keygen never overwrites a key."
    exit 1
fi
mkdir -p "$(dirname "$key_file")"
cd "$UO_ROOT"
"$UO_PYTHON" "$UO_TOOLS/asset_store/run.py" keygen "$key_file"
echo
echo "Send the Public key and Fingerprint lines (never the key file)."
