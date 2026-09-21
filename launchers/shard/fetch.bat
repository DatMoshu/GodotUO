@echo off
REM ============================================================================
REM  Clone ModernUO and apply the GUO patches. Run once on a fresh machine.
REM
REM  A full clone, not a shallow one: ModernUO versions itself with
REM  Nerdbank.GitVersioning, which walks the history and fails the build with
REM  "Shallow clone lacks the objects required to calculate version height".
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

if exist "%UO_SHARD_SRC%\.git" (
    echo [shard] Already cloned: %UO_SHARD_SRC%
    goto :patch
)

echo [shard] Cloning %UO_SHARD_REPO%
git clone "%UO_SHARD_REPO%" "%UO_SHARD_SRC%" || exit /b 1

:patch
for %%P in ("%UO_ROOT%\tools\modernuo\patches\*.patch") do (
    echo [shard] Applying %%~nxP
    git -C "%UO_SHARD_SRC%" apply --check "%%~fP" >nul 2>&1 && (
        git -C "%UO_SHARD_SRC%" apply "%%~fP" || exit /b 1
    ) || echo [shard]   already applied, skipping
)

echo [shard] Done. Next: launchers\shard\build.bat
exit /b 0
