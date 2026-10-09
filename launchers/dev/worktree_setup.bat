@echo off
rem Readies a fresh git worktree: copies your local config across, checks the engine and upstream resolve, and runs one headless import.
rem args: --no-import
REM ============================================================================
REM  Make a fresh git worktree ready to build, run and smoke. No directory
REM  links: the engine and the upstream reference resolve to the main
REM  checkout's copies (UO_GODOT_HOME, UO_UPSTREAM_DIR). Copies your
REM  config.local.bat and deny.local.txt across when missing, checks both
REM  folders resolve, and runs one headless import. See
REM  tools\worktree_setup\run.py.
REM
REM      launchers\dev\worktree_setup.bat
REM      launchers\dev\worktree_setup.bat --no-import
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\worktree_setup\run.py" %*
exit /b %ERRORLEVEL%
