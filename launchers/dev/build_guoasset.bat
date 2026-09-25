@echo off
REM Builds the guoasset MCP server (tools\guoasset): the read-only renderer
REM /parity-check uses as ground truth. Needs the .NET 10 SDK and the upstream
REM checkout (launchers\dev\sync_upstream.bat).
REM
REM The artifacts path keeps every obj\ and bin\ in build\guoasset, including
REM those of the upstream projects it compiles: sources\ stays untouched.
call "%~dp0..\_shared\common.bat" || exit /b 1
if not exist "%UO_SOURCES%\ClassicUO\src\ClassicUO.Assets" (
    echo [guoasset] FATAL: no upstream checkout at "%UO_SOURCES%\ClassicUO"
    echo [guoasset] Fetch it with: launchers\dev\sync_upstream.bat
    exit /b 1
)
echo [guoasset] Building tools\guoasset -^> build\guoasset
dotnet build "%UO_TOOLS%\guoasset\GUO.AssetMcp.csproj" -c Release --artifacts-path "%UO_BUILD%\guoasset" -v q --nologo || exit /b 1
echo [guoasset] OK: %UO_BUILD%\guoasset\bin\GUO.AssetMcp\release\guoasset.dll
exit /b 0
