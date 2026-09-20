@echo off
REM Clears the runtime decode cache (textures/atlases decoded from .mul/.uop).
REM Purely derived data; the client rebuilds it on demand.
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [clean] Clearing %UO_CACHE_DIR%
if exist "%UO_CACHE_DIR%" rmdir /s /q "%UO_CACHE_DIR%"
mkdir "%UO_CACHE_DIR%" >nul 2>&1
echo [clean] Done.
exit /b 0
