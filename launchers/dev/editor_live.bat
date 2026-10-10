@echo off
rem Checks the editor's live tier end to end with two editors and a client on the private shard and prints the result.
rem args: --no-export | --facet 0|1 | --windowed
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\editor_live\run.py" %*
exit /b %ERRORLEVEL%
