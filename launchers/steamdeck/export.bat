@echo off
REM ============================================================================
REM  Exports the Linux x86_64 build headless into build\steamdeck: GUO.x86_64,
REM  GUO.pck and the .NET data folder. Log: build\steamdeck\export.log.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" export %*
exit /b %ERRORLEVEL%
