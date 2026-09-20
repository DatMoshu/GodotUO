@echo off
REM Builds the Godot project's C# assemblies without opening the editor.
REM This is the fast "does it still compile" check.
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [build] Building C# assemblies for %UO_GODOT_PROJECT%
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --build-solutions --quit
exit /b %ERRORLEVEL%
