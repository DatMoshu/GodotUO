@echo off
rem Proves ExtractDecal headless: two PNGs in, decal out, stats printed.
REM ============================================================================
REM  Proves ExtractDecal without a shard or a window: loads two PNGs,
REM  subtracts the capture from the repaint, saves the decal, prints stats.
REM
REM      launchers\dev\extract_test.bat --extract-in IN.png --extract-out OUT.png --extract-dest DECAL.png [--extract-tol 12] [--extract-feather 2]
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --extract-test %*
exit /b %ERRORLEVEL%
