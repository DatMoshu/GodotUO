@echo off
rem Prints whether the private ServUO shard is running and listening on 127.0.0.1:2596.
REM ============================================================================
REM  The private ServUO shard (127.0.0.1:2596): status. See tools\servuo\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\servuo\run.py" status %*
exit /b %ERRORLEVEL%
