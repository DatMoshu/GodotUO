@echo off
REM ============================================================================
REM  Restores the pinned Godot build into tools\godot (it is gitignored).
REM  No-op if the pinned version is already present.
REM ============================================================================
for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"
call "%~dp0..\_shared\config.bat"
set "TARGET=%UO_ROOT%\tools\godot\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%"
if exist "%TARGET%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.exe" (
    echo [fetch] Godot %GODOT_VERSION% already present.
    exit /b 0
)
set "ZIP=%TEMP%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.zip"
set "URL=https://github.com/godotengine/godot/releases/download/%GODOT_VERSION%/Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.zip"
echo [fetch] Downloading %URL%
powershell -NoProfile -Command "Invoke-WebRequest -Uri '%URL%' -OutFile '%ZIP%'" || exit /b 1
echo [fetch] Extracting to %UO_ROOT%\tools\godot
powershell -NoProfile -Command "Expand-Archive -Force -Path '%ZIP%' -DestinationPath '%UO_ROOT%\tools\godot'" || exit /b 1
del "%ZIP%" >nul 2>&1
echo [fetch] Done.
exit /b 0
