@echo off
rem Deletes the project's .godot folder and re-imports headless, rebuilding Godot's class and script cache; prints each step.
REM Forces Godot to re-import assets and rebuild its class/script cache.
REM Use after large file moves or when the editor reports phantom scripts.
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [cache] Removing %UO_GODOT_PROJECT%\.godot
if exist "%UO_GODOT_PROJECT%\.godot" rmdir /s /q "%UO_GODOT_PROJECT%\.godot"
echo [cache] Re-importing...
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --import --quit
set "CACHE_RC=%ERRORLEVEL%"
echo [cache] Done.
exit /b %CACHE_RC%
