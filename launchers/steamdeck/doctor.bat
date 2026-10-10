@echo off
rem Lists what a Linux export needs on this machine and what the Steam Deck has over ssh, with the fix for each miss.
REM ============================================================================
REM  What a Godot 4.7 .NET Linux export needs on this machine, and what the
REM  Deck has (ssh, the client data, a screenshot tool, Desktop mode), with
REM  the fix for each miss. Read docs\steamdeck.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\steamdeck\run.py" doctor %*
exit /b %ERRORLEVEL%
