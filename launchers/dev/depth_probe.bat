@echo off
rem Builds the C#, then draws the same quads through the canvas and the world depth pass and prints whether pixels and depth order agree (PERF-GPU step 1).
REM ============================================================================
REM  The world depth pass of ADR-0007's 2026-10-19 amendment, checked on
REM  synthetic art: pixel parity with UltimaBatcher2D at zooms 0.5-2.5, the
REM  depth order of adjacent keys, and which way ties go. No UO install needed.
REM
REM  Engine flags go first, e.g.  depth_probe.bat --rendering-method gl_compatibility
REM  Deliberately NOT --headless: the dummy renderer never produces a frame.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
call "%~dp0build.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" %* -- --depth-probe
exit /b %ERRORLEVEL%
