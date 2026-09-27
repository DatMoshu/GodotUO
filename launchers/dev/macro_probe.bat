@echo off
REM ============================================================================
REM  Taps each of the touch bar's six macros (Next Target, Attack Last, Last
REM  Target, Last Object, Bandage Self, War/Peace) against fixtures it spawns
REM  with GM commands: two rats that cannot walk, bandages in the pack, the
REM  character's hits lowered. Each check prints a pass/fail line with its
REM  evidence; the exit code is non-zero on any failure. Needs the dev shard
REM  (launchers\shard\run.bat) and a GM account, e.g.
REM    launchers\dev\macro_probe.bat --account NAME --password PASS
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\screenshots" mkdir "%UO_BUILD%\screenshots"
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play --macro-probe --screenshot-dir "%UO_BUILD%\screenshots" --screenshot-name macro_probe %*
exit /b %ERRORLEVEL%
