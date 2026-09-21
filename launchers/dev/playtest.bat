@echo off
REM ============================================================================
REM  Plays the game, on its own, and says whether it worked.
REM
REM  Drives the real client through a real session against the configured
REM  shard -- log in, make or pick a character, walk, open the backpack, move
REM  an item, open the gumps, speak -- and checks each step. Exits 0 when every
REM  check passed and 1 when any did not, so it can fail a build.
REM
REM  Needs a shard: start launchers\shardun.bat in another terminal first.
REM  The last frame lands in build\screenshots.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\screenshots" mkdir "%UO_BUILD%\screenshots"
echo [playtest] Driving a session against %UO_SHARD_HOST%:%UO_SHARD_PORT%
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play --input-probe ^
    --screenshot-dir "%UO_BUILD%\screenshots" %*
if errorlevel 1 (
    echo [playtest] FAILED
    exit /b 1
)
echo [playtest] OK
exit /b 0
