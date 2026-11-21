@echo off
rem Dev shard only: logs in on a staff account, puts a small boat on the water at the Haven docks with the live Water panel open, and leaves the game to you.
rem args: [--account NAME] [client flags]
call "%~dp0..\_shared\common.bat" || exit /b 1
set "ACCT=guoeffects"
if /i "%~1"=="--account" (set "ACCT=%~2" & shift & shift)
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --boat-demo --autologin --account %ACCT% %1 %2 %3 %4 %5 %6
exit /b %ERRORLEVEL%
