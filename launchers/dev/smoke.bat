@echo off
REM ============================================================================
REM  Fast health check: engine present, project imports, C# builds,
REM  client data readable. Run before every commit and in CI.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
set "FAIL=0"

echo [smoke] 1/5 engine
"%GODOT_CONSOLE%" --version || set "FAIL=1"

echo [smoke] 2/5 project imports
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --quit || set "FAIL=1"

echo [smoke] 3/5 C# builds
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --build-solutions --quit || set "FAIL=1"

echo [smoke] 4/5 client data
"%UO_PYTHON%" "%UO_TOOLS%\uodata\run.py" verify --data-dir "%UO_CLIENT_DATA%" --client-version "%UO_CLIENT_VERSION%" --quiet || set "FAIL=1"

REM  Offline mode exercises what the other steps cannot: the ported readers
REM  against the real install, and the resources compiled into the assembly.
REM  Both resolve by name at runtime, so only running it proves they work.
echo [smoke] 5/5 client offline load
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --offline || set "FAIL=1"

echo.
if "%FAIL%"=="1" ( echo [smoke] FAILED & exit /b 1 )
echo [smoke] OK
exit /b 0
