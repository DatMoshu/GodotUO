@echo off
rem Compares the editor's UO World view with a logged-in client's frame of the same cell, pixel by pixel, and writes the comparison.
rem args: --at <x>,<y> | --season <s> | --windowed
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\world_parity\run.py" %*
exit /b %ERRORLEVEL%
