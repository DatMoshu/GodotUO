@echo off
rem The server compatibility lab: runs GUO's scripted cases against another UO server (ModernUO, ServUO, ...) and rewrites the wiki row; doctor checks the setup.
REM ============================================================================
REM  The server compatibility lab: GUO's scripted cases against other UO servers.
REM
REM      launchers\dev\server_lab.bat doctor
REM      launchers\dev\server_lab.bat row modernuo     set up, start, seed, run the
REM                                                   cases, stop, rewrite the wiki
REM
REM  Each lab server is fetched at its pin into the per-user workspace, listens
REM  on 127.0.0.1 only and has its own generated admin account. See
REM  tools\server_lab\README.md. Take the switchboard leases first (build:D for
REM  setup, shard:PORT and godot:runtime for the runs).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\server_lab\run.py" %*
exit /b %ERRORLEVEL%
