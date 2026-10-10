#!/usr/bin/env bash
# Exports the Windows build headless into build/windows/GUO.exe and fails if the executable's icon is not the sigil.
set -euo pipefail
echo "[export] Windows only: the Windows build and its icon check need the Windows export templates and run on Windows. Run launchers\\windows\\export.bat on Windows." >&2
exit 2
