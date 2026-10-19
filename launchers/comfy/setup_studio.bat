@echo off
rem Checks (or installs) the ComfyUI models and custom nodes GUO's art workflows need, then validates them against the server.
rem args: [--check] [--comfy-dir DIR] [--url URL]
REM ============================================================================
REM  One-command ComfyUI setup for end users (tools\comfy\models.json).
REM  --check only reports what is missing; without it, missing node packs are
REM  cloned and missing models downloaded, then every workflow is validated
REM  against the server. Needs no engine: reads config.bat, not common.bat.
REM ============================================================================
for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"
call "%~dp0..\_shared\config.bat"
"%UO_PYTHON%" "%UO_ROOT%\tools\comfy\setup_studio.py" %*
exit /b %ERRORLEVEL%
