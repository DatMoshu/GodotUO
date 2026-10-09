@echo off
rem Runs four scripted clients against the dev shard, tiled 2x2 on screen, and writes their logs, frames and a contact sheet to build/multi_client.
rem args: --only <lanes> | --list | --sound
REM ============================================================================
REM  Run four scripted clients at once, one per quarter of the screen.
REM
REM  session   the ordinary playtest (it starts a fifth client to trade with)
REM  effects   the effects probe
REM  highlight the mesh highlight check
REM  sweep     [go to a Britain street and photograph it
REM
REM  The session lane plays the owner account's last character; the other three
REM  each have a game master account from UO_SHARD_GM_ACCOUNTS, made by the
REM  shard on a headless boot (UO_SHARD_GM_PASSWORD). Sound is off unless --sound is
REM  passed. Exits 0 only when every lane did. Output lands in
REM  build\multi_client\<stamp>\: a log and a frame per lane, a 2x2 contact
REM  sheet and summary.md.
REM
REM      launchers\dev\multi_client.bat
REM      launchers\dev\multi_client.bat --only session,sweep
REM      launchers\dev\multi_client.bat --list
REM
REM  Needs launchers\shard\run.bat going in another terminal.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\multi_client\run.py" %*
exit /b %ERRORLEVEL%
