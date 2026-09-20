@echo off
REM ============================================================================
REM  Clones or updates the read-only ClassicUO reference under sources\.
REM  Then reports which upstream commits landed since the port last synced, so
REM  fixes can be pulled across deliberately rather than silently drifting.
REM
REM      dev\sync_upstream.bat            update + report new commits
REM      dev\sync_upstream.bat --pin      record current upstream as the baseline
REM ============================================================================
call "%~dp0..\_shared\common.bat" 2>nul
if not defined UO_ROOT for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"
if not defined UO_PYTHON set "UO_PYTHON=python"
"%UO_PYTHON%" "%UO_ROOT%\tools\sync_upstream\run.py" --root "%UO_ROOT%" %*
exit /b %ERRORLEVEL%
