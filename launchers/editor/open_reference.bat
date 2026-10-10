@echo off
rem Opens the read-only upstream ClassicUO solution in your default IDE, which detaches (start) so this returns at once.
REM Opens the upstream ClassicUO solution READ-ONLY, as porting reference.
REM Nothing here is built or shipped; it exists to read the original C#.
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_SOURCES%\ClassicUO\ClassicUO.sln" (
    echo [ref] Upstream missing. Run: launchers\dev\sync_upstream.bat
    exit /b 1
)
echo [ref] REFERENCE ONLY - do not edit files under sources\
start "" "%UO_SOURCES%\ClassicUO\ClassicUO.sln"
exit /b 0
