@echo off
REM ============================================================================
REM  Boots the client headless-ish, captures a frame, and writes it to
REM  build\screenshots so a change can be verified visually without a human
REM  watching the window. Used by the QA and art agents.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\screenshots" mkdir "%UO_BUILD%\screenshots"
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --screenshot --screenshot-dir "%UO_BUILD%\screenshots" %*
echo [shot] Output: %UO_BUILD%\screenshots
exit /b %ERRORLEVEL%
