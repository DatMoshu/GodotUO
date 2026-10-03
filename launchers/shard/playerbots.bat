@echo off
setlocal
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_ROOT%\tools\playerbots\run.py" %*
exit /b %ERRORLEVEL%
