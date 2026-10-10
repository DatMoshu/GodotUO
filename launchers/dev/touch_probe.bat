@echo off
rem Runs the client with the touch layer and a synthetic finger against the shard and prints a pass or fail line per gesture.
REM ============================================================================
REM  Runs the client on the desktop with the touch layer on and a synthetic
REM  finger: tap, hold-to-walk, double tap, pinch, the gump bar, long press.
REM  Each check prints a pass/fail line; the exit code is non-zero on any
REM  failure. Needs launchers\shard\run.bat going in another terminal and a
REM  saved account, the same as playtest.bat.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\screenshots" mkdir "%UO_BUILD%\screenshots"
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play --touch-probe --screenshot-dir "%UO_BUILD%\screenshots" --screenshot-name touch_probe %*
exit /b %ERRORLEVEL%
