@echo off
REM ============================================================================
REM  C9, the portrait spike: export a build with --portrait-probe baked in,
REM  install it, launch it. At the login gump and again in the world it turns
REM  the screen to portrait and back, measures both, and holds each state a
REM  few seconds; this photographs every hold (screencap) and writes the
REM  measures to build\android\portrait\portrait_table.md, with the log.
REM  Non-zero if the probe never finishes or the client dies. Set
REM  UO_ANDROID_DEVICE to pick the device when more than one is attached.
REM  Needs the UO data on the device and a reachable shard
REM  (pass --args "--host <this PC's LAN address>").
REM  See docs\android_portrait_spike.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" portrait_probe %*
exit /b %ERRORLEVEL%
