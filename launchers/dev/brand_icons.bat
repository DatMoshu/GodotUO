@echo off
REM ============================================================================
REM  Rebuilds every app icon from design\brand\guo-sigil.png: icon.png,
REM  icon.ico, splash.png and the four Android launcher PNGs. Run it when
REM  the sigil changes, then commit what it wrote. See tools\brand\run.py.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\brand\run.py" %*
exit /b %ERRORLEVEL%
