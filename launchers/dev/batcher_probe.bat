@echo off
REM ============================================================================
REM  Draws through the real UltimaBatcher2D and checks the pixels that come
REM  back. Proves the two halves of ADR-0002 hue packing agree: the C# side in
REM  UltimaBatcher2D.Encode and the GLSL side in uo_hue.gdshader. They can
REM  disagree silently -- a wrong palette row is still a plausible colour.
REM
REM  Deliberately NOT --headless: the dummy renderer never produces a frame,
REM  so a headless run would hang instead of answering.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
REM  Build first. This probe is C#, and running a stale assembly reports the
REM  PREVIOUS run's result -- which looks exactly like a real failure.
call "%~dp0build.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --batcher-probe %*
exit /b %ERRORLEVEL%
