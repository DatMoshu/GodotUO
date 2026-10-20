@echo off
rem Measures GUO's frame rate on this machine at 1920x1080 and 2560x1440 (zoom 2.5) and writes a report to share in Discord; needs the dev shard running.
rem args: [--preset 1080p^|1440p-far] [--size W,H --zoom Z] [--scene NAME] [--doctor] [--as-built]
REM ============================================================================
REM  The perf lab (tools\perf_lab): checks what it needs, builds GUO optimised,
REM  then times fixed scenes (login, open field, Britain bank, forest, dungeon,
REM  a run) with vsync and the frame cap off. Prints the FPS table and writes
REM  build\perf\perf_lab_<time>.json -- the file to paste or attach in Discord:
REM  GPU, driver, CPU, OS and the numbers, no paths and no names.
REM  Start the dev shard first: launchers\shard\run.bat. Wiki: Performance.
REM      launchers\dev\perf_lab.bat
REM      launchers\dev\perf_lab.bat --preset 1440p-far --scene britain-bank
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_lab\run.py" %*
exit /b %ERRORLEVEL%
