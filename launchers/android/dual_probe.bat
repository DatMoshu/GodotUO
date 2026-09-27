@echo off
REM ============================================================================
REM  The second-screen probe: export a build with --dual-probe baked in,
REM  install it, launch it, log in, wait for logcat to say the second screen
REM  is up ("[GUO] dual screen: ok"), photograph BOTH displays into
REM  build\android (dual_main.png, dual_second.png) with the log, then stop
REM  the app. Non-zero if the second screen never comes up or the client dies.
REM  On a device with one display it reports that and passes.
REM  Needs the UO data on the device (python tools\android\run.py push) and a
REM  reachable shard (pass --args "--host <this PC's LAN address>").
REM  See docs\architecture\ADR-0009-second-display.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" dual_probe %*
exit /b %ERRORLEVEL%
