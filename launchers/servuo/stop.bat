@echo off
REM ============================================================================
REM  The private ServUO shard (127.0.0.1:2596): stop. See tools\servuo\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\servuo\run.py" stop %*
exit /b %ERRORLEVEL%
