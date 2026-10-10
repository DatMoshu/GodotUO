@echo off
rem Sets up a fresh clone: fetches the pinned Godot and the upstream ClassicUO reference, then verifies the UO client data.
REM ============================================================================
REM  PIPELINE STEP 00 - one-time setup on a fresh clone.
REM  Fetches the pinned engine and upstream reference, then verifies the config.
REM ============================================================================
REM  Godot first: common.bat refuses to load until the engine is present.
echo [00] Fetching pinned Godot (skipped if already present)...
call "%~dp0..\dev\fetch_godot.bat" || exit /b 1
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [00] Fetching upstream ClassicUO at the reviewed pin...
call "%~dp0..\dev\sync_upstream.bat" --at-pin || exit /b 1
echo [00] Verifying client data...
call "%~dp001_verify_client_data.bat" || exit /b 1
echo.
echo [00] Bootstrap complete. Next: launchers\game\play.bat
exit /b 0
