@echo off
REM ============================================================================
REM  Build ModernUO into tools\modernuo\src\Distribution.
REM  Needs the .NET 10 SDK; ModernUO's own publish script does the work.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

if not exist "%UO_SHARD_SRC%\publish.cmd" (
    echo [shard] Not cloned yet. Run launchers\shard\fetch.bat first.
    exit /b 1
)

pushd "%UO_SHARD_SRC%"
REM  By full path: cmd only looks in the current directory for a script
REM  when NoDefaultCurrentDirectoryInExePath is not set, and it is set in
REM  some shells.
call "%UO_SHARD_SRC%\publish.cmd" release win x64
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" exit /b %RC%
echo [shard] Built: %UO_SHARD_DIST%
exit /b 0
