@echo off
rem Walks every upstream ClassicUO file, classifies how far it is ported and rewrites docs/port_status.md.
REM ============================================================================
REM  PIPELINE STEP 03 - port progress audit.
REM  Walks every ClassicUO source file under sources\ and reports which are
REM  ported, in progress, rewritten on Godot, or not started. Writes the
REM  scoreboard the agents work from.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\port_audit\run.py" ^
    --upstream "%UO_SOURCES%\ClassicUO" ^
    --port "%UO_GODOT_PROJECT%" ^
    --out "%UO_DOCS%\port_status.md" %*
exit /b %ERRORLEVEL%
