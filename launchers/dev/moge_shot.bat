@echo off
rem Proves the -moge gump in the running game: probe login, -moge, select a static, Generate, screenshots, quits.
REM ============================================================================
REM  Proves the -moge gump in the running game: logs in on a probe account,
REM  says the client command -moge, selects the nearest artwork-bearing
REM  static, runs Generate through tools/moge/moge_to_ply.py, waits for the
REM  live replacement splat, saves the frames, quits.
REM
REM      launchers\dev\moge_shot.bat --account guoeffects
REM
REM  Always use a GM probe account (guoeffects, guohighlight, guosweep), never
REM  the player's: the shard refuses a second character from one account.
REM  GUO_MOGE_MODEL defaults to the cached Ruicheng/moge-2-vits-normal (the
REM  full vitl is a hub download); pass --model via GUO_MOGE_MODEL to change it.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not defined GUO_MOGE_MODEL set "GUO_MOGE_MODEL=Ruicheng/moge-2-vits-normal"
if not exist "%UO_BUILD%\screenshots\moge" mkdir "%UO_BUILD%\screenshots\moge"
REM  The login scene saves settings.json, so snapshot the player's profile:
REM  coordinate-typed probe logins used to append to the prefilled account
REM  field and poison it (autologin on, garbage username). --autologin logs
REM  in with exact strings instead; the snapshot is restored whatever happens.
if exist "%LOCALAPPDATA%\GUO\settings.json" copy /y "%LOCALAPPDATA%\GUO\settings.json" "%UO_BUILD%\screenshots\moge\settings.bak" >nul
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --moge-shot --autologin --screenshot-dir "%UO_BUILD%\screenshots\moge" %*
set "RC=%ERRORLEVEL%"
if exist "%UO_BUILD%\screenshots\moge\settings.bak" copy /y "%UO_BUILD%\screenshots\moge\settings.bak" "%LOCALAPPDATA%\GUO\settings.json" >nul
echo [moge-shot] Output: %UO_BUILD%\screenshots\moge
exit /b %RC%
