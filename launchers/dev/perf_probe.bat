@echo off
rem Measures frame time in fixed scenes against the shard and writes build/perf/perf_<label>.md and .json, or compares two labels.
rem args: --label <name> [--args "<client flags>"] | --compare <a> <b>
REM ============================================================================
REM  Frame time in fixed scenes (Epic B): the login screen, an open field,
REM  the Britain bank, a dense forest, a dungeon, and a run through new ground
REM  (land-array uploads, review R1-1). Needs a shard where the
REM  probe account is a GM. Writes build\perf\perf_LABEL.md and .json.
REM      launchers\dev\perf_probe.bat --label baseline
REM      launchers\dev\perf_probe.bat --label batched --args "--batched-world"
REM      launchers\dev\perf_probe.bat --compare baseline batched
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_probe\run.py" %*
exit /b %ERRORLEVEL%
