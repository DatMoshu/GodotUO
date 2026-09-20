@echo off
REM Opens the generated C# solution for the Godot project in your default IDE.
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_GODOT_PROJECT%\UOPort.sln" (
    echo [sln] No solution yet. Build it first: launchers\dev\build.bat
    exit /b 1
)
start "" "%UO_GODOT_PROJECT%\UOPort.sln"
exit /b 0
