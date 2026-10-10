#!/usr/bin/env bash
# Builds a native (MSVC) and a .NET Framework test plugin, plays a session with both loaded and prints whether each saw packets and movement.
set -euo pipefail
echo "[plugin_probe] Windows only: it builds its native plugin with MSVC and its managed one for .NET Framework, which only exist on Windows. Run launchers\\dev\\plugin_probe.bat on Windows." >&2
exit 2
