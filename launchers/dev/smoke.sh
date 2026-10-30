#!/usr/bin/env bash
# Runs the fast health check (engine, import, C# build, client data, offline load, editor add-on, launcher lint) and prints OK or FAILED.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
FAIL=0

echo "[smoke] 1/7 engine"
"$GODOT_CONSOLE" --version || FAIL=1

echo "[smoke] 2/7 project imports"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit || FAIL=1

echo "[smoke] 3/7 C# builds"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --build-solutions --quit || FAIL=1

echo "[smoke] 4/7 client data"
"$UO_PYTHON" "$UO_TOOLS/uodata/run.py" verify --data-dir "${UO_CLIENT_DATA:-}" --client-version "$UO_CLIENT_VERSION" --quiet || FAIL=1

# Offline mode exercises what the other steps cannot: the ported readers
# against the real install, and the resources compiled into the assembly.
echo "[smoke] 5/7 client offline load"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --offline || FAIL=1

echo "[smoke] 6/7 editor add-on (headless; tools/editor_smoke)"
"$UO_PYTHON" "$UO_TOOLS/editor_smoke/run.py" --no-build || FAIL=1

echo "[smoke] 7/7 launchers (tools/launcher_lint)"
"$UO_PYTHON" "$UO_TOOLS/launcher_lint/run.py" || FAIL=1

echo "[smoke] shard launcher secret reload (synthetic; no server)"
"$UO_PYTHON" "$UO_TOOLS/modernuo/test_launcher_secrets.py" || FAIL=1

echo "[smoke] selection seam (synthetic; no shard or install)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Bootstrap/SelectionSeamProbe.tscn || FAIL=1

echo "[smoke] world depth ordering (CPU only; no shard or install)"
dotnet run --project "$UO_ROOT/tools/world_depth_tests/WorldDepthTests.csproj" --artifacts-path "$UO_BUILD_DIR/world-depth-tests" --nologo || FAIL=1

echo "[smoke] diagnostics correctness (fragmented loopback, plugins, compression; no FPS sampling)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://addons/guo_editor/Tests/DiagnosticsFunctionalProbe.tscn || FAIL=1

echo "[smoke] optional audio pack contracts (synthetic; no shard or AI service)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Store/StoreAudioProbe.tscn || FAIL=1

echo "[smoke] optional HD pack contracts (synthetic)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Store/StoreHdProbe.tscn || FAIL=1

echo "[smoke] UO+ ambient particles contract (synthetic; ADR-0042)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Particles/AmbientParticlesProbe.tscn || FAIL=1
echo "[smoke] shared UO+ flags, densities and Touch UI persistence (synthetic; ADR-uoplus-settings, UX-03)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Configuration/UoPlusProbe.tscn || FAIL=1
echo "[smoke] Lighting speed weather state and callback contract (headless; ADR-0048)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/SpeedMode/Weather/SpeedModeWeatherProbe.tscn || FAIL=1
echo "[smoke] Window receiver shaft gates and failure contract (headless; ADR-0048)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/SpeedMode/WindowLight/WindowRayProbe.tscn || FAIL=1
echo "[smoke] Lighting shard clock framing and logout contract (offline; ADR-0048)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/SpeedMode/Shadows/ShardClockProbe.tscn || FAIL=1

echo "[smoke] Realtime lights contract (synthetic; ADR-0039)"
echo "[smoke] Lighting speed surface transport and default-off persistence (headless contract; ADR-0048)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --lighting-speed-probe "--lighting-speed-out=$UO_BUILD_DIR/lighting-speed-probe" --no-focus || FAIL=1
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Lights/RealtimeLightsProbe.tscn || FAIL=1
echo "[smoke] UO+ spell lighting contract (synthetic; no shard or install)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Lights/SpellLightingProbe.tscn || FAIL=1
echo "[smoke] water render modes contract (synthetic; ADR-0037)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Water/WaterProbe.tscn || FAIL=1
echo "[smoke] perf lab report: fields kept, privacy refusal, size (synthetic; tools/perf_lab)"
"$UO_PYTHON" -m pytest "$UO_TOOLS/perf_lab" -q -p no:cacheprovider || FAIL=1

echo "[smoke] water authoring contracts (synthetic; editor only)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://addons/guo_editor/Water/WaterWorkbenchProbe.tscn || FAIL=1

echo "[smoke] PostFX engine regressions (synthetic)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Bootstrap/PostFxEngineProbe.tscn || FAIL=1

echo "[smoke] hosted catalogue (a local Pages branch and loopback HTTP; no network)"
"$UO_PYTHON" "$UO_TOOLS/asset_store/hosted_proof.py" || FAIL=1
echo "[smoke] touch: swipe and two-finger pan scroll lists (synthetic; no shard)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --touch-gesture-probe --pregame-classic --silent || FAIL=1
echo "[smoke] packet mods (synthetic; builds the sample mod, ADR-0038)"
dotnet build "$UO_ROOT/docs/modding/samples/announce/Announce.csproj" -nologo -v q || FAIL=1
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Network/Mods/ModsProbe.tscn || FAIL=1

echo "[smoke] keyboard: key-ups on focus loss, AltGr text (synthetic; no shard)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --login-preflight-probe --pregame-classic --silent || FAIL=1
dotnet run --project "$UO_ROOT/tools/probe_rules_tests/ProbeRulesTests.csproj" --artifacts-path "$UO_BUILD_DIR/probe-rules-tests" --nologo || FAIL=1
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --keyboard-probe --pregame-classic --silent || FAIL=1
echo "[smoke] controller regressions and Set controls (synthetic pad; no shard)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --pad-regression-probe --pregame-classic --silent || FAIL=1

if [ "${GuoLabs:-false}" = "true" ]; then
    echo "[smoke] ordered GPU world Labs pixels (real GPU; ADR-0043)"
    "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --batcher-probe || FAIL=1
    echo "[smoke] UO+ weather Labs contract"
    "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit-after 60 res://src/Labs/Weather/WeatherProbe.tscn || FAIL=1
    "$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" --quit-after 60 res://src/Labs/WaterRain/WaterRainProbe.tscn || FAIL=1
fi

echo
if [ "$FAIL" = "1" ]; then echo "[smoke] FAILED"; exit 1; fi
echo "[smoke] OK"
