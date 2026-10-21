# UO+ weather (L4, non-parity)

Owner scope: 2026-10-20 night weather brief. Three independent, default-off
experiments: rain rings on water, rain-filled tile puddles, and a wet player
silhouette. Tree sway and biome particles are separate lanes.

Build with `dotnet build godot/GUO/GUO.csproj -p:GuoLabs=true` (Debug).
Open **General → Labs: UO+ weather** in Modern Options, or `-uopweather`.
The controls apply immediately and last for this World/session only. They do
not modify upstream weather, profile serialization, packets or picking.
Default builds and every export exclude this code.

Puddles accept a comma-separated list of land graphic IDs (decimal or `0x`
hexadecimal). The optional cobblestone matcher reads the supplied installation's
tiledata name. No game data is distributed. Empty IDs with that matcher disabled
means no puddle tiles. Invalid input leaves the current list intact.

Rain means a nonzero upstream rain/storm-brewing weather event. Water rings
use visible Wet land or static tiles, and work with Classic or styled water. They are separate
pixel primitives. The shared `WeatherLab.RainIntensity(World)` signal reads rain
or storm-brewing Count/70, clamped to 0–1; snow, dry and unavailable weather return
zero. This value is independent of the three effect toggles, which gate consumers.
Puddles fill in 30 seconds and drain in 45 seconds; impacts produce a brief
splash and an expanding elliptical ring. Player wetness rises in 4 seconds and
fades over 18 seconds after rain. Body, equipment and mount alpha are collected
from the drawn frames; edge drops draw after equipment inside the existing
sorted mobile pass. Sitting, dead and hidden player silhouettes are skipped.

Bounds: 512 surface slots, 4096 candidate tiles/frame, at most 64 objects per
tile in each of two scans (incomplete cells are skipped), 40 rain impacts/second,
0–32 new impacts and 0–32 body drops/frame, at most eight candidate alpha samples
per budgeted body drop and 32 frame layers. Delta is capped at 250 ms after a
stall. A separate ceiling allows at most 1024 animation-alpha queries/frame.
Surface cache collisions skip the later cell in that frame. Travel beyond
the cache can discard old puddles. Facet changes clear all transient effects.
Puddles and water impacts skip known roofs or raised surfaces in the same cell.
Wet statics above sloped dry land remain eligible for water rings. Stretched
dry slopes without water and sitting-body deformation are outside
this initial Labs implementation. Rain itself still follows ClassicUO.

Dependency doctor (no extra software, services or models):

```text
dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true
godot-console --headless --path godot/GUO --quit-after 60 res://src/Labs/Weather/WeatherProbe.tscn
```

If the weather command or scene class is missing, rerun this doctor with the
repository's pinned mono Godot and .NET SDK. See `tools/worktree_setup` if the
engine/upstream cannot resolve. Smoke: set `GuoLabs=true` before
`launchers/dev/smoke.bat` or `launchers/dev/smoke.sh` to include this contract.

Scenario: `tools/scenarios/client/weather_lab.md`. Synthetic surface render:
`res://src/Labs/Weather/WeatherVisualProbe.tscn`; `GUO_WEATHER_PROOF` optionally
names its PNG output. This proves primitive rendering, not live-world appearance.

For a repeatable live proof, launch a scratch GM client with
`GUO_WEATHER_PROOF_DIR` set to the evidence run's `stills/` folder and issue
`-uopweather proof`. The explicit test moves the character to the Britain docks,
selects its current land graphic for puddles, sets local rain, captures off/rain/
drying/dry frames and checks effect state over about 90 seconds before quitting.
It never starts automatically and never changes the server weather. The test
requires a GM character on a shard you control. Failure reports name this README.
