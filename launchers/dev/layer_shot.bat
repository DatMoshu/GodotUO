@echo off
REM ============================================================================
REM  Photographs a terrain layer: logs in on a probe account, says the shard
REM  [go, lets the scene settle, saves two frames, quits. For iterating on
REM  overlay/underlay rendering without a human at the screen.
REM
REM      launchers\dev\layer_shot.bat --account guoeffects --layer-at "1438 1696 0"
REM
REM  Always use a GM probe account (guoeffects, guohighlight, guosweep), never
REM  the player's: the shard refuses a second character from one account.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\screenshots\layer_shot" mkdir "%UO_BUILD%\screenshots\layer_shot"
REM  The login scene saves settings.json, so snapshot the player's profile:
REM  coordinate-typed probe logins used to append to the prefilled account
REM  field and poison it (autologin on, garbage username). --autologin logs
REM  in with exact strings instead; the snapshot is restored whatever happens.
if exist "%LOCALAPPDATA%\GUO\settings.json" copy /y "%LOCALAPPDATA%\GUO\settings.json" "%UO_BUILD%\screenshots\layer_shot\settings.bak" >nul
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --layer-shot --autologin --screenshot-dir "%UO_BUILD%\screenshots\layer_shot" %*
set "RC=%ERRORLEVEL%"
if exist "%UO_BUILD%\screenshots\layer_shot\settings.bak" copy /y "%UO_BUILD%\screenshots\layer_shot\settings.bak" "%LOCALAPPDATA%\GUO\settings.json" >nul
echo [layer-shot] Output: %UO_BUILD%\screenshots\layer_shot
exit /b %RC%
