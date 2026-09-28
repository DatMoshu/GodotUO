@echo off
REM ============================================================================
REM  Frame time in five fixed scenes (Epic B): the login screen, an open field,
REM  the Britain bank, a dense forest and a dungeon. Needs a shard where the
REM  probe account is a GM. Writes build\perf\perf_LABEL.md and .json.
REM      launchers\dev\perf_probe.bat --label baseline
REM      launchers\dev\perf_probe.bat --label batched --args "--batched-world"
REM      launchers\dev\perf_probe.bat --compare baseline batched
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_probe\run.py" %*
exit /b %ERRORLEVEL%
