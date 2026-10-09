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
    for /f "delims=" %%P in ('dir /b /a-d /o-n "%UO_ROOT%\tools\modernuo\patches\*.patch"') do (
        call :patch_off "%UO_ROOT%\tools\modernuo\patches\%%P" || exit /b 1
    )
)
git -C "%UO_SHARD_SRC%" checkout -q --detach "%SHARD_WANT%" || (
    echo [shard] Checkout refused: the checkout has other local changes. See tools\modernuo\README.md, "Retired".
    exit /b 1
)
echo [shard] At the pin %SHARD_WANT:~0,9%

:patch
for %%P in ("%UO_ROOT%\tools\modernuo\patches\*.patch") do (
    call :patch_on "%%~fP" || exit /b 1
)

echo [shard] Done. Next: launchers\shard\build.bat
exit /b 0

REM --- one patch on: applies -> apply; comes off in reverse -> already on; else fatal
:patch_on
echo [shard] Applying %~nx1
git -C "%UO_SHARD_SRC%" apply --check "%~1" >nul 2>&1
if not errorlevel 1 (
    git -C "%UO_SHARD_SRC%" apply "%~1" || exit /b 1
    exit /b 0
)
git -C "%UO_SHARD_SRC%" apply -R --check "%~1" >nul 2>&1
if not errorlevel 1 (
    echo [shard]   already applied, skipping
    exit /b 0
)
REM  Neither applies nor comes off: the checkout holds something else (an older
REM  copy of this patch, local edits). Show git's reason and stop; never build on it.
git -C "%UO_SHARD_SRC%" apply --check "%~1"
echo [shard] FATAL: %~nx1 does not apply to the checkout at %SHARD_WANT:~0,9%, and is not already applied; stopping
exit /b 1

REM --- one patch off before the pin moves; if it will not reverse, reset its files
:patch_off
git -C "%UO_SHARD_SRC%" apply -R --check "%~1" >nul 2>&1
if not errorlevel 1 (
    git -C "%UO_SHARD_SRC%" apply -R "%~1" || exit /b 1
    exit /b 0
)
REM  Not cleanly reversible (an older copy of the patch, or edits on top): put its
REM  files back to HEAD; a file the patch adds is not in HEAD, so it goes.
echo [shard]   %~nx1 does not come off in reverse; resetting its files
for /f "usebackq tokens=2,*" %%A in (`git -C "%UO_SHARD_SRC%" apply --numstat "%~1"`) do (
    call :reset_file "%%B" || exit /b 1
)
exit /b 0

:reset_file
git -C "%UO_SHARD_SRC%" cat-file -e "HEAD:%~1" 2>nul
if not errorlevel 1 (
    git -C "%UO_SHARD_SRC%" checkout -q -- "%~1" || exit /b 1
    exit /b 0
)
set "SHARD_FILE=%~1"
set "SHARD_FILE=%SHARD_FILE:/=\%"
if exist "%UO_SHARD_SRC%\%SHARD_FILE%" del /f /q "%UO_SHARD_SRC%\%SHARD_FILE%"
exit /b 0
