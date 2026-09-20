@echo off
REM Console build of the pinned Godot: blocks until exit and prints to stdout/stderr.
REM Agents, scripts and CI should call THIS, not godot.cmd.
setlocal
set "GODOT_DIR=%~dp0Godot_v4.7.2-stable_mono_win64"
set "GODOT_EXE=%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64_console.exe"
if not exist "%GODOT_EXE%" (
    echo [godot] Pinned Godot build not found: "%GODOT_EXE%"
    echo [godot] Re-run: launchers\dev\fetch_godot.bat
    exit /b 1
)
endlocal & "%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64_console.exe" %*
