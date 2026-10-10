@echo off
rem Downloads the pinned Godot build into tools/godot (or UO_GODOT_HOME) unless it is already there, and prints each step.
REM ============================================================================
REM  Restores the pinned Godot build into tools\godot (it is gitignored), or
REM  into UO_GODOT_HOME when that is set.
REM  No-op if the pinned version is already present.
REM ============================================================================
for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"
call "%~dp0..\_shared\config.bat"
if not defined UO_GODOT_HOME set "UO_GODOT_HOME=%UO_ROOT%\tools\godot"
set "TARGET=%UO_GODOT_HOME%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%"
if exist "%TARGET%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.exe" (
    echo [fetch] Godot %GODOT_VERSION% already present.
    exit /b 0
)
set "ZIP=%TEMP%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.zip"
set "URL=https://github.com/godotengine/godot/releases/download/%GODOT_VERSION%/Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.zip"
echo [fetch] Downloading %URL%
powershell -NoProfile -Command "Invoke-WebRequest -Uri '%URL%' -OutFile '%ZIP%'" || exit /b 1
echo [fetch] Extracting to %UO_GODOT_HOME%
powershell -NoProfile -Command "Expand-Archive -Force -Path '%ZIP%' -DestinationPath '%UO_GODOT_HOME%'" || exit /b 1
del "%ZIP%" >nul 2>&1
echo [fetch] Done.
exit /b 0
