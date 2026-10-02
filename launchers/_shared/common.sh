# shellcheck shell=bash
# ============================================================================
#  GUO - shared launcher logic (Linux/macOS twin of common.bat)
#
#  NOT A LAUNCHER. Every .sh under launchers/ sources this first:
#
#      . "$(dirname "$0")/../_shared/common.sh" || exit 1
#
#  Settings are still written in config.local.bat / config.bat, on every OS:
#  tools/shellenv resolves them (environment, config.local.bat, config.bat)
#  exactly as tools/guo/config.py does for every other tool, and exports them.
#  Afterwards UO_ROOT, UO_GODOT_PROJECT, GODOT_EXE, GODOT_CONSOLE, UO_PYTHON
#  and everything in config.bat are set.
# ============================================================================

UO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [ -z "${UO_PYTHON:-}" ]; then
    if command -v python3 >/dev/null 2>&1; then UO_PYTHON=python3; else UO_PYTHON=python; fi
fi
export UO_PYTHON

# The central shared config (UO_COMMON_CONFIG) is a .bat; on this side it is a
# .sh of exports, sourced first so the project config and the environment win.
if [ -n "${UO_COMMON_CONFIG:-}" ]; then
    if [ -f "$UO_COMMON_CONFIG" ]; then
        # shellcheck disable=SC1090
        . "$UO_COMMON_CONFIG"
    else
        echo "[common] WARNING: UO_COMMON_CONFIG set but not found: \"$UO_COMMON_CONFIG\""
    fi
fi

_guo_env="$("$UO_PYTHON" "$UO_ROOT/tools/shellenv/run.py")" || {
    echo "[common] FATAL: could not resolve the configuration (tools/shellenv)"
    return 1 2>/dev/null || exit 1
}
eval "$_guo_env"
unset _guo_env

# The pinned engine needs a .NET SDK new enough for the project's C#; one
# installed per-user (dotnet-install.sh) is found here when not on PATH.
if [ -z "${DOTNET_ROOT:-}" ] && ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
fi

if [ "${GUO_NEEDS_GODOT:-1}" = "1" ] && [ ! -x "$GODOT_EXE" ]; then
    echo "[common] FATAL: Godot not found at \"$GODOT_EXE\""
    echo "[common] Fetch the pinned build with: launchers/dev/fetch_godot.sh"
    echo "[common] Or set GODOT_EXE in the environment"
    return 1 2>/dev/null || exit 1
fi

mkdir -p "$UO_CACHE_DIR" 2>/dev/null || true
