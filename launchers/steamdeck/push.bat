@echo off
REM ============================================================================
REM  Copies build\steamdeck onto the Deck (rsync over ssh when both ends
REM  have it, a tar stream otherwise) into UO_DECK_INSTALL_DIR, and writes
REM  guo.sh there, pointing at UO_DECK_CLIENT_DATA and the shard. Never
REM  pushes the UO client data. Pass --host <shard LAN address> if the
REM  shard runs on this PC (127.0.0.1 would be the Deck itself).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" push %*
exit /b %ERRORLEVEL%
