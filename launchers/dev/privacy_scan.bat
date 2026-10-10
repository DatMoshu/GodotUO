@echo off
rem Scans the tracked files for LAN addresses, personal emails, home folders and machine paths and prints each hit.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\privacy_scan\run.py" %*
exit /b %ERRORLEVEL%
