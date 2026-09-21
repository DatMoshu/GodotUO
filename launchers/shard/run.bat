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

echo [shard] %UO_SHARD_NAME% on %UO_SHARD_HOST%:%UO_SHARD_PORT%  (Ctrl-C to stop)
pushd "%UO_SHARD_DIST%"
"%UO_SHARD_DIST%\ModernUO.exe"
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
