@echo off
rem Writes the private ServUO shard's configuration (127.0.0.1:2596, your UO data folder) and prints what it set.
REM ============================================================================
REM  The private ServUO shard (127.0.0.1:2596): configure. See tools\servuo\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\servuo\run.py" configure %*
exit /b %ERRORLEVEL%
