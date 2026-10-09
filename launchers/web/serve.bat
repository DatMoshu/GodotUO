@echo off
rem Serves build/web on UO_WEB_PORT with the cross-origin isolation headers in the foreground until Ctrl+C.
REM ============================================================================
REM  Serves build\web on UO_WEB_PORT with the cross-origin isolation
REM  headers a threaded Godot web export needs. Ctrl+C stops it.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\web\run.py" serve %*
exit /b %ERRORLEVEL%
