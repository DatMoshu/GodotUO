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

echo "[smoke] selection seam (synthetic; no shard or install)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Bootstrap/SelectionSeamProbe.tscn || FAIL=1

echo "[smoke] optional audio pack contracts (synthetic; no shard or AI service)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Store/StoreAudioProbe.tscn || FAIL=1

echo "[smoke] optional HD pack contracts (synthetic)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Store/StoreHdProbe.tscn || FAIL=1

echo "[smoke] UO+ ambient particles contract (synthetic; ADR-0042)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Particles/AmbientParticlesProbe.tscn || FAIL=1
echo "[smoke] Realtime lights contract (synthetic; ADR-0039)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Lights/RealtimeLightsProbe.tscn || FAIL=1
echo "[smoke] water render modes contract (synthetic; ADR-0037)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" res://src/Render/Water/WaterProbe.tscn || FAIL=1
echo "[smoke] perf lab report: fields kept, privacy refusal, size (synthetic; tools/perf_lab)"
"$UO_PYTHON" -m pytest "$UO_TOOLS/perf_lab" -q -p no:cacheprovider || FAIL=1

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
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --keyboard-probe --pregame-classic --silent || FAIL=1
echo "[smoke] controller regressions and Set controls (synthetic pad; no shard)"
"$GODOT_CONSOLE" --headless --path "$UO_GODOT_PROJECT" -- --pad-regression-probe --pregame-classic --silent || FAIL=1

echo
if [ "$FAIL" = "1" ]; then echo "[smoke] FAILED"; exit 1; fi
echo "[smoke] OK"
