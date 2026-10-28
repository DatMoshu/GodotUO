@echo off
rem Checks dependencies and output configuration for the Labs mobile frame inspector.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_ROOT%\tools\mobile_sync\doctor.py" %*
exit /b %ERRORLEVEL%
