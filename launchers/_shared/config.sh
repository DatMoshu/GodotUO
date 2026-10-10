# shellcheck shell=bash
# ============================================================================
#  GUO - project configuration (Linux/macOS twin of config.bat)
#
#  NOT A LAUNCHER. common.sh sources it. Same keys as config.bat, same order
#  of resolution (first hit wins):
#      1. an already-set environment variable
#      2. config.local.sh, next to this file (yours; gitignored)
#      3. this file
#      4. the central shared config (UO_COMMON_CONFIG), if you use one
#
#  Every line is `: "${NAME:=VALUE}"`, which only sets NAME when it is unset
#  or empty -- the shell's `if not defined`. tools/guo/config.py parses this
#  file the same narrow way, so a tool run outside a launcher agrees with it.
#  YOUR OWN PATHS go in config.local.sh: copy config.local.sh.example.
#
#  Paths use forward slashes. A Windows default config.bat builds on
#  %LOCALAPPDATA% or %APPDATA% is empty here: the code then picks this OS's
#  folder (XDG_DATA_HOME, XDG_CONFIG_HOME), as it does for config.bat.
#  tools/launcher_lint checks this file has exactly config.bat's keys.
# ============================================================================

# Everything set below (and in config.local.sh) is exported, as cmd's set is.
set -a
_guo_shared_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
if [ -f "$_guo_shared_dir/config.local.sh" ]; then . "$_guo_shared_dir/config.local.sh"; fi
unset _guo_shared_dir
: "${UO_ROOT:=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# --- Pinned engine -----------------------------------------------------------
: "${GODOT_VERSION:=4.7.2-stable}"
# Empty = this OS's build (mono_linux_x86_64, mono_macos.universal, ...).
: "${GODOT_FLAVOR:=}"
# UO_GODOT_HOME / UO_UPSTREAM_DIR / GODOT_EXE: leave unset for tools/godot and
# sources (a git worktree uses the main checkout's), or set them in
# config.local.sh.

# --- UO client data ----------------------------------------------------------
# UO_CLIENT_DATA has no default: set it in config.local.sh or the environment.
: "${UO_CLIENT_VERSION:=7.0.107.76}"

# --- Runtime cache -----------------------------------------------------------
# Empty = $XDG_DATA_HOME/GUO/cache (~/.local/share/GUO/cache).
: "${UO_CACHE_DIR:=}"

# --- Agent request queue -----------------------------------------------------
# GUO_EDITOR_MCP_TOKEN is environment-only; never put it here.
: "${GUO_EDITOR_MCP_PORT:=}"
: "${GUO_EDITOR_MCP_PYTHON:=}"
# Empty = $XDG_CONFIG_HOME/guo/agent_queue.db.
: "${UO_AGENT_QUEUE:=}"

# --- World project (the editor) ----------------------------------------------
: "${UO_WORLD_PROJECT:=$UO_ROOT/build/world/default}"

# --- Map generator (tools/mapgen, ADR-0030) ----------------------------------
# Empty = next to the cache (~/.local/share/GUO/mapgen).
: "${UO_MAPGEN_DATA:=}"
: "${UO_EDITOR_LIVE_HOST:=127.0.0.1}"
: "${UO_EDITOR_LIVE_PORT:=2595}"
: "${UO_EDITOR_NAME:=${USER:-editor}}"

# --- Shard to connect to -----------------------------------------------------
: "${UO_SHARD_HOST:=127.0.0.1}"
: "${UO_SHARD_PORT:=2593}"

# --- Local dev shard (ModernUO) ----------------------------------------------
: "${UO_SHARD_NAME:=GUO Dev}"
: "${UO_SHARD_REPO:=https://github.com/modernuo/ModernUO.git}"
: "${UO_SHARD_REF:=d4531cd94b739613155225c234900de9f47d2c88}"
: "${UO_SHARD_SRC:=$UO_ROOT/tools/modernuo/src}"
: "${UO_SHARD_DIST:=$UO_SHARD_SRC/Distribution}"
: "${UO_PLAYERBOTS_DIR:=$UO_ROOT/build/playerbots}"
: "${UO_PLAYERBOTS_PORT:=2640}"
: "${UO_SHARD_BIND:=127.0.0.1}"
# The owner and GM passwords are generated on the shard's first run into the
# per-user workspace; never set them here.
: "${UO_SHARD_OWNER:=guoprobe}"
: "${UO_SHARD_GM_ACCOUNTS:=guoeffects,guohighlight,guosweep}"
: "${UO_SHARD_UPDATE_RANGE:=72}"

# --- Android (optional) ------------------------------------------------------
# Android Studio's default SDK folder on Linux.
: "${UO_ANDROID_SDK:=$HOME/Android/Sdk}"
: "${UO_ANDROID_JDK:=${JAVA_HOME:-}}"
# Godot's own data folder on Linux.
: "${UO_ANDROID_KEYSTORE:=$HOME/.local/share/godot/keystores/debug.keystore}"
: "${UO_ANDROID_KEYSTORE_USER:=androiddebugkey}"
: "${UO_ANDROID_KEYSTORE_PASSWORD:=android}"
: "${UO_ANDROID_PACKAGE:=org.guo.client}"
: "${UO_ANDROID_DEVICE:=}"
: "${UO_ANDROID_CLIENT_DATA:=/sdcard/Android/data/$UO_ANDROID_PACKAGE/files/uo}"
: "${UO_ANDROID_SECOND_DISPLAY:=}"
: "${UO_ANDROID_ACCOUNT:=}"

# --- Web (optional) ----------------------------------------------------------
: "${UO_WEB_PORT:=8060}"
: "${UO_WS_BRIDGE_PORT:=2594}"
: "${UO_WEB_LAN:=0}"
: "${UO_WEB_LAN_HOST:=}"
# The community web-export build is a Windows binary (tools/godot_web); set
# its path in config.local.sh if you have one for this OS.
: "${UO_WEB_GODOT:=}"

# --- Steam Deck (optional) ---------------------------------------------------
: "${UO_DECK_HOST:=}"
: "${UO_DECK_USER:=deck}"
: "${UO_DECK_SSH_KEY:=}"
: "${UO_DECK_KNOWN_HOSTS:=}"
: "${UO_DECK_INSTALL_DIR:=~/GUO}"
: "${UO_DECK_CLIENT_DATA:=~/UO}"
: "${UO_DECK_ACCOUNT:=}"

# --- Layout sources (optional, read-only) ------------------------------------
: "${UO_LAYOUT_CDDA_DIR:=}"
: "${UO_LAYOUT_ZOMBOID_DIR:=}"

# --- Python ------------------------------------------------------------------
: "${UO_PYTHON:=python3}"

# --- Logging -----------------------------------------------------------------
# DEBUG | INFO | WARN | ERROR
: "${UO_LOG_LEVEL:=INFO}"

# --- GUO Asset Store ---------------------------------------------------------
: "${UO_STORE_DIR:=build/store_cdn}"
: "${UO_STORE_URL:=http://127.0.0.1:18865}"
: "${UO_STORE_SIGNING_KEY:=}"
: "${UO_STORE_CATALOGUE_ID:=local}"
: "${UO_STORE_CATALOGUE_TITLE:=Local GUO packs}"
: "${UO_STORE_BASE_URL:=}"

# --- Art pipeline (ADR-0029) -------------------------------------------------
: "${UO_ART_EXCHANGE:=$UO_ROOT/build/art_exchange}"
: "${UO_PIXELORAMA:=}"
: "${UO_PINTA:=}"
: "${UO_COMFY_URL:=http://127.0.0.1:8188}"
: "${UO_COMFY_WORKFLOWS:=$UO_ROOT/build/art_exchange/workflows}"

set +a
