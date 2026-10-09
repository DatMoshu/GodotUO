@echo off
rem Starts ClassicUO and GUO side by side on one monitor at the same tile on the dev shard and leaves both running for you to compare.
rem args: --monitor <name> --place <spot> | --at <x> <y> | --list-monitors
REM ============================================================================
REM  ClassicUO and GUO live, side by side on one monitor, in the same place.
REM
REM  ClassicUO takes the left half on the owner account, GUO the right half on
REM  the first of UO_SHARD_GM_ACCOUNTS. Both are sent to one tile and the shard
REM  is pinned to daylight. Both stay running: walk around and compare.
REM  ClassicUO is driven by typing, so leave the desktop alone while it logs in.
REM
REM      launchers\dev\side_by_side.bat --monitor FireTV --place britain-street
REM      launchers\dev\side_by_side.bat --at 1602 1591
REM      launchers\dev\side_by_side.bat --list-monitors
REM
REM  Needs launchers\shard\run.bat going in another terminal.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\side_by_side\run.py" %*
exit /b %ERRORLEVEL%
