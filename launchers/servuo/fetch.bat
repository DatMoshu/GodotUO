@echo off
rem Clones the private ServUO shard's source at its pin and prints what it fetched.
REM ============================================================================
REM  The private ServUO shard (127.0.0.1:2596): fetch. See tools\servuo\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\servuo\run.py" fetch %*
exit /b %ERRORLEVEL%
