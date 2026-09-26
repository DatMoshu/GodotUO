@echo off
REM ============================================================================
REM  Starts the installed client on the device and streams its logcat,
REM  filtered to the engine, the .NET runtime and crashes. Ctrl+C stops the
REM  stream; the app keeps running.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" run %*
exit /b %ERRORLEVEL%
