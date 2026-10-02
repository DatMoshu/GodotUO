@echo off
REM ============================================================================
REM  Clone ModernUO at the pin (UO_SHARD_REF) and apply the GUO patches.
REM  Run once on a fresh machine, and again after the pin moves: it takes the
REM  patches off, checks out the new pin and puts them back. Stop the shard
REM  first, and rebuild with build.bat afterwards.
REM
REM  A full clone, not a shallow one: ModernUO versions itself with
REM  Nerdbank.GitVersioning, which walks the history and fails the build with
REM  "Shallow clone lacks the objects required to calculate version height".
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

if exist "%UO_SHARD_SRC%\.git" (
    echo [shard] Already cloned: %UO_SHARD_SRC%
) else (
    echo [shard] Cloning %UO_SHARD_REPO%
    git clone --no-checkout "%UO_SHARD_REPO%" "%UO_SHARD_SRC%" || exit /b 1
)

git -C "%UO_SHARD_SRC%" cat-file -e "%UO_SHARD_REF%^{commit}" 2>nul || git -C "%UO_SHARD_SRC%" fetch origin || exit /b 1
set "SHARD_HEAD="
set "SHARD_WANT="
for /f %%H in ('git -C "%UO_SHARD_SRC%" rev-parse -q --verify HEAD 2^>nul') do set "SHARD_HEAD=%%H"
for /f %%H in ('git -C "%UO_SHARD_SRC%" rev-parse "%UO_SHARD_REF%^{commit}"') do set "SHARD_WANT=%%H"
if not defined SHARD_WANT (
    echo [shard] The pin %UO_SHARD_REF% is not in %UO_SHARD_REPO%.
    exit /b 1
)
if /i "%SHARD_HEAD%"=="%SHARD_WANT%" goto :patch

if defined SHARD_HEAD (
    echo [shard] Moving from %SHARD_HEAD:~0,9% to the pin %SHARD_WANT:~0,9%: taking the patches off first
    for %%P in ("%UO_ROOT%\tools\modernuo\patches\*.patch") do (
        git -C "%UO_SHARD_SRC%" apply -R --check "%%~fP" >nul 2>&1 && git -C "%UO_SHARD_SRC%" apply -R "%%~fP"
    )
)
git -C "%UO_SHARD_SRC%" checkout -q --detach "%SHARD_WANT%" || (
    echo [shard] Checkout refused: the checkout has other local changes. See tools\modernuo\README.md, "Retired".
    exit /b 1
)
echo [shard] At the pin %SHARD_WANT:~0,9%

:patch
for %%P in ("%UO_ROOT%\tools\modernuo\patches\*.patch") do (
    echo [shard] Applying %%~nxP
    git -C "%UO_SHARD_SRC%" apply --check "%%~fP" >nul 2>&1 && (
        git -C "%UO_SHARD_SRC%" apply "%%~fP" || exit /b 1
    ) || echo [shard]   already applied, skipping
)

echo [shard] Done. Next: launchers\shard\build.bat
exit /b 0
