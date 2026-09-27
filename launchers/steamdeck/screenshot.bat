@echo off
REM ============================================================================
REM  Photographs the Deck's screen (spectacle, or grim) over ssh into
REM  build\steamdeck\screenshot.png. Desktop mode only.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" screenshot %*
exit /b %ERRORLEVEL%
