@echo off
rem Checks what L4 map paging needs: dotnet, the engine-free paging tests, the page manifest in GUO_LABS_PAGING and a Labs build; --probe also runs the headless loader probe.
rem args: [--probe] [--fixture <dir>]
call "%~dp0..\_shared\common.bat" || exit /b 1
set "FAIL=0"
set "TESTS=%UO_ROOT%\tools\paging_tests\PagingTests.csproj"
where dotnet >nul 2>&1 || ( echo [paging-doctor] dotnet is missing: install the .NET 8 SDK. & exit /b 1 )
if "%~1"=="--fixture" (
    if "%~2"=="" ( echo [paging-doctor] --fixture needs a folder outside the checkout, e.g. under UO_BUILD_DIR. & exit /b 2 )
    dotnet run --project "%TESTS%" --artifacts-path "%UO_BUILD_DIR%\paging-tests" -- --fixture "%~2"
    exit /b %ERRORLEVEL%
)
echo [paging-doctor] 1/4 paging core tests (no engine, no client data)
dotnet run --project "%TESTS%" --artifacts-path "%UO_BUILD_DIR%\paging-tests" || set "FAIL=1"
echo [paging-doctor] 2/4 page manifest
if defined GUO_LABS_PAGING (
    dotnet run --project "%TESTS%" --artifacts-path "%UO_BUILD_DIR%\paging-tests" -- --validate "%GUO_LABS_PAGING%" || set "FAIL=1"
) else (
    echo [paging-doctor] GUO_LABS_PAGING is not set: paging stays off. Set it to a page manifest; "--fixture <dir>" writes a synthetic one.
)
echo [paging-doctor] 3/4 Labs build (Debug, GuoLabs=true)
dotnet build "%UO_GODOT_PROJECT%\GUO.csproj" -c Debug -p:GuoLabs=true -nologo -v q || set "FAIL=1"
if "%~1"=="--probe" (
    echo [paging-doctor] 4/4 headless loader probe against UO_CLIENT_DATA
    "%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --quit-after 120 res://src/Labs/Paging/PagingProbe.tscn || set "FAIL=1"
) else (
    echo [paging-doctor] 4/4 loader probe skipped; add --probe to run it ^(starts headless Godot^)
)
if "%FAIL%"=="1" ( echo [paging-doctor] FAILED: see godot\GUO\src\Labs\Paging\README.md & exit /b 1 )
echo [paging-doctor] OK
exit /b 0
