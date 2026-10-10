@echo off
rem Opens a Godot window that checks a canvas item can read what was drawn under it (the effect blend modes) and prints the answer.
REM ============================================================================
REM  Asks whether a RenderingServer canvas item can read what was drawn under
REM  it, via canvas_item_set_copy_to_backbuffer and hint_screen_texture. That
REM  is the only way to express ClassicUO effect blend states Godot has no
REM  fixed mode for -- a reverse subtract, and 2*src*dst.
REM
REM  Deliberately NOT --headless: the dummy renderer never produces a frame.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" --script res://dev/blend_probe.gd %*
exit /b %ERRORLEVEL%
