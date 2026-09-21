@echo off
REM ============================================================================
REM  Measures what one sprite costs to add to a texture atlas at each page
REM  size. Godot has no partial texture upload, so the page size IS the cost
REM  of adding a 44x44 sprite to it, and upstream ClassicUO picks 4096.
REM
REM  Deliberately NOT --headless: the dummy renderer uploads nothing, so it
REM  would measure nothing and report that it was fast.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" --script res://dev/atlas_probe.gd %*
exit /b %ERRORLEVEL%
