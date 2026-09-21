@echo off
REM ============================================================================
REM  Photograph the same handful of places every time, so a renderer change
REM  can be looked at rather than argued about.
REM
REM  Every visual fault reported so far was found by eye, not by a check:
REM  furniture painted over a roof, journal text across a world map, a tree
REM  over a building. The probe cannot see any of those. What it can do is
REM  stand in the same places each time, so two sweeps can be compared.
REM
REM  The spots are chosen to be different from each other: a town in
REM  daylight, a forest with buildings in it, a coastline, a dungeon mouth,
REM  and a town at ground level among houses with roofs.
REM
REM  Needs launchers\shard\run.bat going in another terminal. One client run per
REM  spot, so it takes a couple of minutes.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

set "SWEEP=%UO_BUILD%\screenshots\sweep"
if not exist "%SWEEP%" mkdir "%SWEEP%"

call :shot minoc-town       2500 560
call :shot yew-forest        633 858
call :shot britain-coast    1497 1790
call :shot despise-mouth    5401 629
call :shot britain-street   1602 1591

echo [sweep] Output: %SWEEP%
exit /b 0

:shot
echo [sweep] %~1 at %~2 %~3
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play ^
    --screenshot-dir "%SWEEP%" ^
    --screenshot-name "%~1" ^
    --shard-command "[go %~2 %~3"
if errorlevel 1 echo [sweep] %~1 FAILED
exit /b 0
