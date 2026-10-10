@echo off
rem Logs ClassicUO and GUO into the dev shard at the same five spots and writes one comparison sheet per spot; the ClassicUO half types into its window, so it needs --allow-foreground and a desktop left alone.
rem args: --only guo|cuo | --place <name> | --allow-foreground
REM ============================================================================
REM  Stand ClassicUO and GUO in the same places and photograph both.
REM
REM  The only way to settle what the port draws wrongly is to have the
REM  original next to it. This logs each client into the dev shard with the
REM  same account, the same character and the same profile, sends both to
REM  the same five spots, and writes a sheet per spot with one above the
REM  other.
REM
REM  Needs launchers\shard\run.bat going in another terminal. The
REM  ClassicUO half drives its window by hand -- it brings it to the front
REM  and types into it with the desktop's keyboard -- so it only runs with
REM  --allow-foreground, and you leave the desktop alone while it does.
REM  Without the flag the default run stops with a message; --only guo
REM  never touches a window and needs nothing.
REM
REM  Pass --only guo, --only cuo or --place <name> to redo part of it.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\ab_compare\run.py" %*
exit /b %ERRORLEVEL%
