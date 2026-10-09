@echo off
rem Starts GUO in the Steam Deck's Desktop-mode session over ssh; output goes to guo.log on the Deck.
rem args: --wait <seconds> | --args "<client flags>"
REM ============================================================================
REM  Starts guo.sh in the Deck's Desktop-mode session over ssh; output goes
REM  to guo.log next to it. --wait N follows the log for N seconds;
REM  --args "..." passes client flags. Game mode: add it to Steam instead
REM  (python tools\steamdeck\run.py shortcut prints how).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" run %*
exit /b %ERRORLEVEL%
