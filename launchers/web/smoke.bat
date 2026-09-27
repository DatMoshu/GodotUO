@echo off
REM ============================================================================
REM  Export, serve, load the page in a headless browser and wait for the
REM  engine's first console line. Pass --no-export to reuse build\web.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\web\run.py" smoke %*
exit /b %ERRORLEVEL%
