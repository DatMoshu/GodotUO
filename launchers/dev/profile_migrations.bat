@echo off
rem Builds GUO and runs the profile migration ladder headless for the desktop, mobile and web profiles, printing each result.
rem args: --no-build
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\profile_migrations\run.py" %*
exit /b %ERRORLEVEL%
