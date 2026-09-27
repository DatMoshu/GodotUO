@echo off
REM ============================================================================
REM  The Android smoke: export a build with --login-probe-stay baked in,
REM  install it, launch it, wait for logcat to say the login gump rendered,
REM  pull a screenshot and the log into build\android, then stop the app.
REM  Non-zero if the login gump never appears or the client dies.
REM  Needs the UO data on the device first (python tools\android\run.py push).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" smoke %*
exit /b %ERRORLEVEL%
