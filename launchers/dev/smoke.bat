@echo off
REM ============================================================================
REM  Fast health check: engine present, project imports, C# builds,
REM  client data readable, and the editor add-on (headless, about 20 s).
REM  Run before every commit.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
set "FAIL=0"

echo [smoke] 1/6 engine
"%GODOT_CONSOLE%" --version || set "FAIL=1"

echo [smoke] 2/6 project imports
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --quit || set "FAIL=1"

echo [smoke] 3/6 C# builds
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --build-solutions --quit || set "FAIL=1"

echo [smoke] 4/6 client data
"%UO_PYTHON%" "%UO_TOOLS%\uodata\run.py" verify --data-dir "%UO_CLIENT_DATA%" --client-version "%UO_CLIENT_VERSION%" --quiet || set "FAIL=1"

REM  Offline mode exercises what the other steps cannot: the ported readers
REM  against the real install, and the resources compiled into the assembly.
REM  Both resolve by name at runtime, so only running it proves they work.
echo [smoke] 5/6 client offline load
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --offline || set "FAIL=1"

echo [smoke] 6/6 editor add-on (headless; tools\editor_smoke)
"%UO_PYTHON%" "%UO_TOOLS%\editor_smoke\run.py" --no-build || set "FAIL=1"

echo.
if "%FAIL%"=="1" ( echo [smoke] FAILED & exit /b 1 )
echo [smoke] OK
exit /b 0
