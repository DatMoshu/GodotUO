@echo off
rem Builds the C#, then opens the GUO project in the pinned Godot editor, which detaches (start) so this returns at once.
rem args: -- <Godot flags>
REM Opens the GUO Godot project in the pinned editor, with the UO docks
REM (addons\guo_editor: UO Assets on the left, UO Inspector on the right).
REM
REM The C# is built first: the addon is C# in the game assembly, and an
REM editor that opens before that assembly exists cannot load it.
REM
REM Anything after the launcher's name goes to Godot, e.g.
REM   open_project.bat -- --guo-editor-smoke build\editor_smoke\launcher
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [editor] Building C# for %UO_GODOT_PROJECT%
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --build-solutions --quit
if errorlevel 1 (
    echo [editor] C# build failed; the UO docks will not load. See the output above.
)
echo [editor] Opening %UO_GODOT_PROJECT% in Godot %GODOT_VERSION%
start "" "%GODOT_EXE%" --editor --path "%UO_GODOT_PROJECT%" %*
exit /b 0
