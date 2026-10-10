#!/usr/bin/env bash
# Lists what a Windows export needs (templates, the sigil icon and splash, project settings) and what this machine has.
set -euo pipefail
echo "[doctor] Windows only: the Windows build and its icon check need the Windows export templates and run on Windows. Run launchers\\windows\\doctor.bat on Windows." >&2
exit 2
