# ADR: UO+ weather in Labs

## Status

Owner-requested scope (2026-10-20 night brief); implementation proposed for PR
review. No approval to promote to L1 or merge is implied.

## Decision

### Shipping amendment — owner sprint brief, 2026-10-22 (WX-01–03)

The three effects are promoted to L1 under `src/Render/Weather`, using the
existing shared UO+ persisted flags and individual densities. They remain off
by default, including exports. The Labs commands, explicit local override and
visual/proof harness remain Debug-only. The sections below describe the original
L4 implementation; this amendment supersedes its build exclusion and signal.

Every shard 0x65 packet supplies an independent UO+ weather signal, including
same-type count changes and zero-count clears. A marked reader-copy seam leaves
classic weather dispatch unchanged. Rain and both storm types produce
`clamp(count / 70, 0, 1)`; snow/unknown/clear produce zero. Signal expiry follows
the six-minute classic weather lifetime independently of classic rendering.
World/facet cleanup removes the signal and visual cache. No packet is sent,
rewritten or suppressed. Labs may explicitly override only its own signal.

Intensity scales impact admission, wetting speed and puddle filling speed;
drying and drainage retain 18/45-second lifetimes. Native art stays nearest;
the existing shared depth queue and non-pickable surface objects are unchanged.
Normal builds retain primitive water rings; the existing Labs styled-water
wave integration stays guarded pending that lane's separate shipping work.
Snow does not fill puddles; a frost/snow-dust visual is deferred.

L1 proof: `client.weather`, normal smoke WeatherProbe, shared UoPlusProbe,
normal/ExportRelease builds, and the live acceptance in `client/weather.md`.
The existing two bounded object scans retain wet statics over sloped land and
reject incomplete chains. Live Britain and Yew checks must be reported separately
from those source facts. No new external dependency or doctor is needed.

Classify weather additions as L4: default-off, session-local experiments compiled
only with `GuoLabs=true` in Debug. Each effect has an independent checkbox in a
panel registered through the shared Labs command/Options hook. No new profile
fields, downloaded assets, dependencies, shaders or external processes.

Surface effects enter the existing RenderLists queue using GameObject's shared
CalculateDepthZ. Player drops use the union of current animation alpha masks and
draw after equipment inside its sorted draw call. Sampling remains nearest.
All ported-file seams are marked `PORT DEVIATION (GUO)` and compile away outside
Labs. The standard weather renderer is untouched.

## Context and alternatives

The owner requested water rain rings, filling/draining cobblestone and listed-tile
puddles, and a fading wet-player outline. Directly extending the water shader
would couple this lane to simultaneous water work and omit Classic water.
A fullscreen overlay would paint rings across foreground objects. Independent
pixel primitives in the existing painter queue avoid both problems.

The follow-up supplies a shared, toggle-independent rain intensity signal for
the water lane: rain and storm-brewing Count/70 clamped to 0–1, zero otherwise.
Styled-water simulation integration and duplicate-ring suppression travel
together in the water lane's PR; this signal alone does not alter rendering.
Tile inspection is bounded to 64 objects per pass and skips incomplete cells
rather than guessing whether a roof or higher surface blocks the rain.

## ADR dependencies and engine compatibility

Depends on ADR-0001/0002 (scene seam and canvas batcher) and the existing L4 build
policy. Uses existing UltimaBatcher2D primitives and Animations.PixelCheck;
Godot 4.7.2 mono compilation verifies the Window/controls APIs. No new engine API
is required for the effects. The synthetic visual probe verifies primitive
rendering; live-world visual acceptance remains a separate scenario step.

## Consequences and verification

Owner-requested water follow-up (2026-10-21): with a styled water mode, the
existing default-off water-ring toggle can inject rain into the existing wave
field. WATER-07 promotes this consumer to `src/Render/WaterRain` in normal and
export builds, on the reviewed weather promotion dependency (PR #120,
`55d9e3a46b75c5d112b36350eb65272178717f61`). Both Rain rings and Water ripples
UO+ toggles are required, with independent densities. `WeatherLab.RainIntensity(World)` is the shared
0..1 input. Bounded, loaded-cell/height/cover validation precedes each impact;
the field's existing update uploads it. A current-world/map/frame predicate
suppresses primitive water rings while simulation is active. Classic retains
the primitive fallback. No new shader, profile field or export path is added.
See [the water rain doctor](../../godot/GUO/src/Render/WaterRain/README.md) and
[scenario](../../tools/scenarios/client/water_rain.md).

The cache and per-frame budgets bound work. Puddles can be evicted by travel or
cache collision; this is visual state only. Tile IDs are chosen locally and
validated without shipping proprietary tile lists. Settings deliberately reset
when a World is replaced. Sitting silhouettes remain future
work. No gameplay, network weather or picking semantics change.

Run WeatherProbe for defaults, independent toggles, fill/drain, fade, ring expiry,
delta caps, budget clamps and tile validation. Default/off parity is structural:
default builds exclude Labs and the guarded seams. Live off/on screenshots and
roof/occlusion checks are defined in the scenario. Promotion to L1 requires
profile persistence, the L1 documentation contract and measured off-state parity.
