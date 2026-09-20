@echo off
REM ============================================================================
REM  UO_Port - project configuration
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
REM ============================================================================

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
if not defined UO_CLIENT_DATA       set "UO_CLIENT_DATA=C:\Path\To\Ultima Online Classic"

REM  Client version the data above corresponds to. Drives which file formats
REM  and packet layouts the readers expect. See docs\data_formats.md.
if not defined UO_CLIENT_VERSION    set "UO_CLIENT_VERSION=7.0.107.76"

REM --- Runtime cache ------------------------------------------------------
REM  Where decoded textures/atlases are cached. Safe to delete at any time;
REM  it is rebuilt on demand. Keep it OFF the repo tree.
if not defined UO_CACHE_DIR         set "UO_CACHE_DIR=%LOCALAPPDATA%\UO_Port\cache"

REM --- Shard to connect to ------------------------------------------------
if not defined UO_SHARD_HOST        set "UO_SHARD_HOST=127.0.0.1"
if not defined UO_SHARD_PORT        set "UO_SHARD_PORT=2593"

REM --- Python -------------------------------------------------------------
if not defined UO_PYTHON            set "UO_PYTHON=python"

REM --- Logging ------------------------------------------------------------
REM  DEBUG | INFO | WARN | ERROR
if not defined UO_LOG_LEVEL         set "UO_LOG_LEVEL=INFO"
