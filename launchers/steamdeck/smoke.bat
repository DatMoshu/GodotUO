@echo off
rem Exports, pushes and starts GUO on the Steam Deck, waits for the login gump, photographs it into build/steamdeck/smoke.png and fails if it never appears.
REM ============================================================================
REM  The Steam Deck smoke: export, push, start with --login-probe-stay, wait
REM  for guo.log to say the login gump rendered, photograph the screen into
REM  build\steamdeck\smoke.png, stop the client. Non-zero if the gump never
REM  appears or the client dies. Needs the UO data on the Deck first
REM  (docs\steamdeck.md says where).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" smoke %*
exit /b %ERRORLEVEL%
