@echo off
REM ============================================================================
REM  Plays a long session and checks the client is the same at the end of it.
REM
REM  The ordinary playtest measures three hundred frames standing still, which
REM  says the renderer can draw what is in front of it and nothing about a
REM  session. This runs the same session and then keeps playing -- walking a
REM  square, so land, statics and mobiles are loaded and dropped the whole
REM  time -- and compares the last stretch with the first, in frame time, in
REM  object count and in memory.
REM
REM  Five minutes by default; pass a number of seconds to change it:
REM      launchers\dev\endurance.bat 900
REM
REM  Needs a shard: start launchers\shard\run.bat in another terminal first.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
set "ENDURE=%~1"
if "%ENDURE%"=="" set "ENDURE=300"
echo [endurance] Playing on for %ENDURE% seconds after the usual session
call "%~dp0playtest.bat" --endure %ENDURE%
if errorlevel 1 (
    echo [endurance] FAILED
    exit /b 1
)
echo [endurance] OK
exit /b 0
