@echo off
rem Opens a Godot window that checks a canvas mesh can carry a per-vertex value (ARRAY_CUSTOM0) for land lighting and prints the answer.
REM ============================================================================
REM  Asks whether a canvas item can carry a per-vertex value beyond position,
REM  UV and colour -- ARRAY_CUSTOM0 on a mesh drawn with canvas_item_add_mesh.
REM
REM  ClassicUO lights stretched land per vertex, and the batcher's packed
REM  colour has no channel left to carry it. If CUSTOM0 works, the world mesh
REM  keeps terrain shading; if it does not, the shading has to be quantised
REM  into the spare bits of the hue index.
REM
REM  Deliberately NOT --headless: the dummy renderer never produces a frame.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" --script res://dev/mesh_probe.gd %*
exit /b %ERRORLEVEL%
