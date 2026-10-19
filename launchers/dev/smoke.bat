@echo off
rem Runs the fast health check (engine, import, C# build, client data, offline load, editor add-on, launcher lint) and prints OK or FAILED.
setlocal
REM  Godot's console wrapper waits for descendants; Roslyn must exit with the build.
set "UseSharedCompilation=false"
REM ============================================================================
REM  Fast health check: engine present, project imports, C# builds,
REM  client data readable, and the editor add-on (headless, about 20 s).
REM  Run before every commit.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
set "FAIL=0"

echo [smoke] 1/7 engine
"%GODOT_CONSOLE%" --version || set "FAIL=1"

echo [smoke] 2/7 project imports
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --quit || set "FAIL=1"

echo [smoke] 3/7 C# builds
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" --build-solutions --quit || set "FAIL=1"

echo [smoke] 4/7 client data
"%UO_PYTHON%" "%UO_TOOLS%\uodata\run.py" verify --data-dir "%UO_CLIENT_DATA%" --client-version "%UO_CLIENT_VERSION%" --quiet || set "FAIL=1"

REM  Offline mode exercises what the other steps cannot: the ported readers
REM  against the real install, and the resources compiled into the assembly.
REM  Both resolve by name at runtime, so only running it proves they work.
echo [smoke] 5/7 client offline load
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --offline || set "FAIL=1"

echo [smoke] 6/7 editor add-on (headless; tools\editor_smoke)
"%UO_PYTHON%" "%UO_TOOLS%\editor_smoke\run.py" --no-build || set "FAIL=1"

echo [smoke] 7/7 launchers (tools\launcher_lint)
"%UO_PYTHON%" "%UO_TOOLS%\launcher_lint\run.py" || set "FAIL=1"

echo [smoke] selection seam (synthetic; no shard or install)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" res://src/Bootstrap/SelectionSeamProbe.tscn || set "FAIL=1"

echo [smoke] optional audio pack contracts (synthetic; no shard or AI service)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" res://src/Store/StoreAudioProbe.tscn || set "FAIL=1"

echo [smoke] optional HD pack contracts (synthetic)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" res://src/Store/StoreHdProbe.tscn || set "FAIL=1"

echo [smoke] perf lab report: fields kept, privacy refusal, size (synthetic; tools\perf_lab)
"%UO_PYTHON%" -m pytest "%UO_TOOLS%\perf_lab" -q -p no:cacheprovider || set "FAIL=1"

echo [smoke] PostFX engine regressions (synthetic)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" res://src/Bootstrap/PostFxEngineProbe.tscn || set "FAIL=1"

echo [smoke] keyboard: key-ups on focus loss, AltGr text (synthetic; no shard)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --keyboard-probe --pregame-classic --silent || set "FAIL=1"
echo [smoke] controller regressions and Set controls (synthetic pad; no shard)
"%GODOT_CONSOLE%" --headless --path "%UO_GODOT_PROJECT%" -- --pad-regression-probe --pregame-classic --silent || set "FAIL=1"

echo.
if "%FAIL%"=="1" ( echo [smoke] FAILED & exit /b 1 )
echo [smoke] OK
exit /b 0
