@echo off
rem Runs the Python tool self-tests CI runs (no client data, no shard) and prints a pass or fail line per test file.
rem args: --list | --only <text>
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\selftest\run.py" %*
exit /b %ERRORLEVEL%
