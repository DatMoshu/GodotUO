@echo off
REM Opens the UO_Port Godot project in the pinned editor.
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [editor] Opening %UO_GODOT_PROJECT% in Godot %GODOT_VERSION%
start "" "%GODOT_EXE%" --editor --path "%UO_GODOT_PROJECT%"
exit /b 0
