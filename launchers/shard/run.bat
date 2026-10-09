@echo off
REM ============================================================================
REM  Run the local dev shard.
REM
REM  Writes Configuration\modernuo.json and expansion.json from the templates
REM  in tools\modernuo\config on first run, filling in UO_CLIENT_DATA and the
REM  shard name and port from config.bat. ModernUO otherwise asks for them at
REM  a console prompt, and it refuses to prompt when stdin is redirected --
REM  which is every scripted or agent-driven run.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

if not exist "%UO_SHARD_DIST%\ModernUO.exe" (
    echo [shard] Not built yet. Run launchers\shard\build.bat first.
    exit /b 1
)

"%UO_PYTHON%" "%UO_ROOT%\tools\modernuo\configure.py" || exit /b 1

REM  configure.py may just have generated the passwords: read them again so the
REM  shard's headless boot (patch 0001) sets the owner and GM accounts to them.
set "UO__SECRETS=%UO_WORKSPACE_DIR%"
if not defined UO__SECRETS set "UO__SECRETS=%LOCALAPPDATA%\GUO"
if exist "%UO__SECRETS%\shard\secrets.bat" call "%UO__SECRETS%\shard\secrets.bat"
set "UO__SECRETS="

REM  The editor bridge rides along on the dev shard itself (single-shard
REM  setup, ADR-0012): the UO Shard dock's Live connects to it on 2595, the
REM  same port it has always used, so no dock settings change.
if not defined GUO_BRIDGE_PORT set "GUO_BRIDGE_PORT=2595"
if not defined GUO_BRIDGE_SHARD set "GUO_BRIDGE_SHARD=%UO_SHARD_NAME%"

echo [shard] %UO_SHARD_NAME% on %UO_SHARD_BIND%:%UO_SHARD_PORT%  (Ctrl-C to stop)
pushd "%UO_SHARD_DIST%"
"%UO_SHARD_DIST%\ModernUO.exe"
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
