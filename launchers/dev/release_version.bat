@echo off
rem Checks or synchronizes the client version from version.json; pass check or sync.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\release\run.py" %*
exit /b %ERRORLEVEL%
