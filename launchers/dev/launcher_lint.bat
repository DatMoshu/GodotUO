@echo off
rem Checks every launcher is a .bat/.sh pair with a description line, the right line endings and the executable bit, and prints each problem.
rem args: --list | --json
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\launcher_lint\run.py" %*
exit /b %ERRORLEVEL%
