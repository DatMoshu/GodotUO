@echo off
rem Sets up, builds, runs or checks the optional PlayerBots ModernUO shard on its own port; run stays in the foreground until Ctrl+C.
rem args: setup|build|run|play|populate|smoke|status
setlocal
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_ROOT%\tools\playerbots\run.py" %*
exit /b %ERRORLEVEL%
