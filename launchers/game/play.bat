@echo off
REM ============================================================================
REM  THE LAUNCHER. Runs the GUO client.
REM
REM      launchers\game\play.bat                 connect to the configured shard
REM      launchers\game\play.bat --offline       no shard; data/render smoke only
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

echo [play] Godot        : %GODOT_VERSION% %GODOT_FLAVOR%
echo [play] Project      : %UO_GODOT_PROJECT%
echo [play] UO client data: %UO_CLIENT_DATA%
echo [play] Shard        : %UO_SHARD_HOST%:%UO_SHARD_PORT%
echo.

if not exist "%UO_CLIENT_DATA%\tiledata.mul" (
    echo [play] FATAL: no UO client data at "%UO_CLIENT_DATA%"
    echo [play] Point UO_CLIENT_DATA at your Ultima Online install in
    echo [play]   launchers\_shared\config.bat
    exit /b 1
)

"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- %*
exit /b %ERRORLEVEL%
