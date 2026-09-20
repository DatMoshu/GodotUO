@echo off
REM Stable entry point for the pinned Godot editor/runtime.
REM Put this folder on PATH so `godot` resolves for humans, agents and CI.
setlocal
set "GODOT_DIR=%~dp0Godot_v4.7.2-stable_mono_win64"
set "GODOT_EXE=%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64.exe"
if not exist "%GODOT_EXE%" (
    echo [godot] Pinned Godot build not found: "%GODOT_EXE%"
    echo [godot] Re-run: launchers\dev\fetch_godot.bat
    exit /b 1
)
endlocal & "%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64.exe" %*
