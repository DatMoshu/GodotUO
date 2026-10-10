@echo off
rem Pre-decodes UO art into the runtime cache headless so the first visit to a place does not hitch; needs step 01's manifest.
REM ============================================================================
REM  PIPELINE STEP 02 - pre-warm the runtime decode cache.
REM  Optional. The client builds this lazily on demand; running it up front
REM  just removes the first-visit hitch. Safe to delete %UO_CACHE_DIR% anytime.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_BUILD%\client_manifest.json" (
    echo [02] No manifest. Run pipeline\01_verify_client_data.bat first.
    exit /b 1
)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --warm-cache %*
exit /b %ERRORLEVEL%
