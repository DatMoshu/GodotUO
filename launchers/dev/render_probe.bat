@echo off
REM ============================================================================
REM  Verifies the engine behaviour ADR-0002 depends on: that a canvas item's
REM  per-quad modulate can carry ClassicUO's hue vector to the shader, and how
REM  many bits of it survive. Both answers were surprising once already, so
REM  this is a measurement rather than a comment in a header.
REM
REM  Deliberately NOT --headless: the dummy renderer never produces a frame,
REM  so a headless run hangs instead of answering.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" --script res://dev/hue_probe.gd %*
exit /b %ERRORLEVEL%
