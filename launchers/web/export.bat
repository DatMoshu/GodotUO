@echo off
REM ============================================================================
REM  Exports the web build headless into build\web. Fails today (see
REM  doctor); the exact refusal lands in build\web\export.log.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\web\run.py" export %*
exit /b %ERRORLEVEL%
