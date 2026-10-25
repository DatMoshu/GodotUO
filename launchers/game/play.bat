@echo off
rem Runs the GUO client in a window against the configured shard (or the first-run wizard when no UO data is set) and stays open until the game exits.
rem args: --offline | --frames <n> | <client flags>
REM ============================================================================
REM  THE LAUNCHER. Runs the GUO client.
REM
REM      launchers\game\play.bat                 connect to the configured shard
REM      launchers\game\play.bat --offline       no shard; data/render smoke only
REM      launchers\game\play.bat --frames 400    run 400 frames and quit
REM
REM  Anything else is passed to the client. Engine flags are not: --frames is
REM  here because Godot's --quit-after has to come BEFORE the -- separator,
REM  and everything after it is the client's. Other engine flags (a scripted run's
REM  --write-movie and --resolution) come in the GUO_ENGINE_ARGS variable.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

REM  Which data the client reads (ADR-0021): a custom data folder
REM  (UO_CUSTOM_DATA), then the UO install (environment, config.local.bat, the
REM  platform default), else the first-run wizard. tools\datasources decides and
REM  writes the result as set lines; exit 3 means "no valid data", which is not
REM  an error: the client is started anyway and opens the wizard.
set "GUO_DATA_ENV=%UO_BUILD%\datasources\play_env.bat"
"%UO_PYTHON%" "%UO_TOOLS%\datasources\run.py" check --bat "%GUO_DATA_ENV%"
set "GUO_DATA_RC=%ERRORLEVEL%"
if "%GUO_DATA_RC%"=="3" (
    echo [play] No valid UO data yet: the client will open the first-run wizard.
) else if not "%GUO_DATA_RC%"=="0" (
    echo [play] FATAL: tools\datasources failed ^(exit %GUO_DATA_RC%^)
    exit /b 1
)
call "%GUO_DATA_ENV%"

echo [play] Godot        : %GODOT_VERSION% %GODOT_FLAVOR%
echo [play] Project      : %UO_GODOT_PROJECT%
echo [play] UO client data: %UO_CLIENT_DATA%
echo [play] Data source  : %UO_DATA_SOURCE%
echo [play] Shard        : %UO_SHARD_HOST%:%UO_SHARD_PORT%
echo.

REM  Forward the original command line with %*: CMD's %1/shift parsing treats
REM  unquoted equals signs as separators (--gpu-world=off became two arguments).
REM  Python preserves those tokens and moves --frames before Godot's separator.
set "GUO_ARGS="
if defined UO_FILES_OVERRIDE set "GUO_ARGS= --files-override "%UO_FILES_OVERRIDE%""
"%UO_PYTHON%" "%UO_TOOLS%\play\run.py" "%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" %GUO_ENGINE_ARGS% --%GUO_ARGS% %*
exit /b %ERRORLEVEL%
