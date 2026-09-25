@echo off
REM ============================================================================
REM  THE LAUNCHER. Runs the GUO client.
REM
REM      launchers\game\play.bat                 connect to the configured shard
REM      launchers\game\play.bat --offline       no shard; data/render smoke only
REM      launchers\game\play.bat --frames 400    run 400 frames and quit
REM
REM  Anything else is passed to the client. Engine flags are not: --frames is
REM  here because Godot's --quit-after has to come BEFORE the -- separator,
REM  and everything after it is the client's.
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
    echo [play]   launchers\_shared\config.local.bat  (copy it from config.local.bat.example)
    exit /b 1
)

REM  Pull --frames N out of the arguments and turn it into Godot's own
REM  --quit-after, which has to sit before the separator.
set "GUO_QUIT_AFTER="
set "GUO_ARGS="
:parse
if "%~1"=="" goto run
if /I "%~1"=="--frames" (
    set "GUO_QUIT_AFTER=--quit-after %~2"
    shift
    shift
    goto parse
)
set "GUO_ARGS=%GUO_ARGS% %1"
shift
goto parse

:run
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" %GUO_QUIT_AFTER% --%GUO_ARGS%
exit /b %ERRORLEVEL%
