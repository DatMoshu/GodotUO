@echo off
REM ============================================================================
REM  GUO - project configuration
REM
REM  THIS IS THE ONLY FILE YOU SHOULD EDIT to point the project at your machine.
REM  Every launcher and tool reads its paths from here.
REM
REM  Resolution order for every setting (first hit wins):
REM      1. an already-set environment variable
REM      2. this file
REM      3. the central shared config (%UO_COMMON_CONFIG%), if you use one
REM
REM  Because of (1), nothing here overwrites a value you exported yourself, so
REM  CI and one-off overrides work without editing this file.
REM
REM  YOUR OWN PATHS go in config.local.bat, next to this file. It is gitignored,
REM  so your install location never ends up in a commit or a diff. Copy
REM  config.local.bat.example to start one. It is read before the defaults
REM  below, so anything it sets wins over them (and loses to the environment,
REM  as long as it keeps the `if not defined` guard).
REM ============================================================================

if exist "%~dp0config.local.bat" call "%~dp0config.local.bat"

REM --- Pinned engine ------------------------------------------------------
REM  Version string must match the folder under tools\godot.
if not defined GODOT_VERSION        set "GODOT_VERSION=4.7.2-stable"
if not defined GODOT_FLAVOR         set "GODOT_FLAVOR=mono_win64"

REM  Leave GODOT_EXE unset to use the pinned build in tools\godot.
REM  Set it to an absolute path to use a Godot installed elsewhere.
REM  if not defined GODOT_EXE       set "GODOT_EXE=C:\Path\To\Godot.exe"

REM --- UO client data -----------------------------------------------------
REM  Folder holding the .mul / .uop / .idx files the client reads at runtime.
REM  This is YOUR legally-obtained Ultima Online install. Nothing is copied
REM  into this repo and nothing here is ever committed.
REM  There is no default: set it in config.local.bat, or in the environment.
REM  if not defined UO_CLIENT_DATA   set "UO_CLIENT_DATA=C:\Path\To\Ultima Online Classic"

REM  Client version the data above corresponds to. Drives which file formats
REM  and packet layouts the readers expect. See docs\data_formats.md.
if not defined UO_CLIENT_VERSION    set "UO_CLIENT_VERSION=7.0.107.76"

REM --- Runtime cache ------------------------------------------------------
REM  Where decoded textures/atlases are cached. Safe to delete at any time;
REM  it is rebuilt on demand. Keep it OFF the repo tree.
if not defined UO_CACHE_DIR         set "UO_CACHE_DIR=%LOCALAPPDATA%\GUO\cache"

REM --- Shard to connect to ------------------------------------------------
if not defined UO_SHARD_HOST        set "UO_SHARD_HOST=127.0.0.1"
if not defined UO_SHARD_PORT        set "UO_SHARD_PORT=2593"

REM --- Local dev shard (ModernUO) -----------------------------------------
REM  The server the client is developed against. See tools\modernuo\README.md.
REM  Set UO_SHARD_HOST above to something else to play on a remote shard; none
REM  of this is needed then.
if not defined UO_SHARD_NAME        set "UO_SHARD_NAME=GUO Dev"
if not defined UO_SHARD_REPO        set "UO_SHARD_REPO=https://github.com/modernuo/ModernUO.git"
if not defined UO_SHARD_SRC         set "UO_SHARD_SRC=%UO_ROOT%\tools\modernuo\src"
if not defined UO_SHARD_DIST        set "UO_SHARD_DIST=%UO_SHARD_SRC%\Distribution"

REM  The dev shard's owner account. On a headless boot the shard makes sure
REM  this account exists and has owner access, which is what lets the world be
REM  generated and administered from the client -- ModernUO takes its commands
REM  in game, not at the console. Local dev shard only; not a credential.
if not defined UO_SHARD_OWNER       set "UO_SHARD_OWNER=guoprobe"
if not defined UO_SHARD_OWNER_PASSWORD set "UO_SHARD_OWNER_PASSWORD=guoprobe"

REM  Accounts for scripted clients that run beside the owner (multi_client.bat):
REM  the shard refuses a second character from one account, and "[go" takes
REM  staff access. Made on a headless boot with game master access; each one's
REM  password is its name. Comma-separated. Local dev shard only.
if not defined UO_SHARD_GM_ACCOUNTS  set "UO_SHARD_GM_ACCOUNTS=guoeffects,guohighlight,guosweep"

REM  How far from a player the shard bothers to send items and mobiles. UO's
REM  own answer is 18 tiles, which was a little more than a 640x480 screen and
REM  is a fraction of a modern one: the client draws map art some 70 tiles out,
REM  so doors, signs, decoration and NPCs stop dead in a circle while the
REM  terrain carries on, and things at its edge appear and vanish as you walk.
REM  72 covers a 4K window. Lower it if the shard struggles; 18 is what a
REM  production shard sends, and is what you want if you are checking parity.
if not defined UO_SHARD_UPDATE_RANGE set "UO_SHARD_UPDATE_RANGE=72"

REM --- Python -------------------------------------------------------------
if not defined UO_PYTHON            set "UO_PYTHON=python"

REM --- Logging ------------------------------------------------------------
REM  DEBUG | INFO | WARN | ERROR
if not defined UO_LOG_LEVEL         set "UO_LOG_LEVEL=INFO"
