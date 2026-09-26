@echo off
REM ============================================================================
REM  Compare a ClassicUO render dump with a GUO one taken in the same place.
REM
REM  Both clients write a dump when "renderdump NAME" is said near them, if
REM  started with GUO_RENDER_DUMP_DIR set, which launchers\dev\side_by_side.bat
REM  does. This reads build\render_dump\NAME\{cuo,guo}.json and writes diff.md.
REM
REM      launchers\dev\render_diff.bat NAME
REM      launchers\dev\render_diff.bat --list
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\render_diff\run.py" %*
exit /b %ERRORLEVEL%
