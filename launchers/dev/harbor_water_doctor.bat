@echo off
rem Checks the optional Labs Rust water solver and its callable entry point.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\harbor_water\doctor.py"
exit /b %ERRORLEVEL%
