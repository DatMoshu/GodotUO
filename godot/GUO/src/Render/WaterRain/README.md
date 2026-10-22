# Rain ripples on styled water (L1, UO+, non-parity)

Promoted for WATER-07 on the exact weather promotion dependency. Normal Debug
and exported clients use the shared **UO+ effects** panel. Enable **Weather:
rain rings** and **Water ripples and wakes**, and select a non-Classic water
style. Both flags default off and keep separate densities. Classic water, or
styled water with the ripple simulation off, retains the opted-in primitive
ring fallback. Puddles and wet-player effects remain independent.

The shared input is `WeatherLab.RainIntensity(World)`: 0..1, rain, storm or brewing
weather count divided by 70, zero for dry/snow/missing weather. Effect toggles
gate consumers rather than the signal. The weather seat owns this provider.

Rain stamps run once inside `WaterRipples.Update`, after the field follows the
player and before its existing simulation/upload. No second wave update or
texture is added. The field steps at its existing 20 Hz. Disabling water drops
or ending rain stops new impacts; existing waves decay naturally alongside
boat/mobile ripples. Map/style resets discard fractional rain credit, and the
first frame or a teleport cannot generate an arrival burst.

At full intensity and 100% Rain rings density, up to 40 candidate impacts/second
are distributed within 18 cells of the player. The weather drop budget also applies, capped at eight
attempts/frame. Delta is capped at 250 ms. Dry/covered/unknown attempts consume
credit, so travelling onto water never releases a backlog. Each candidate
inspects at most 128 loaded tile objects (1024/frame maximum), accepts the
highest Wet land/static only if its height matches the current water mask,
and rejects higher ground, roof/surface statics and multis. Unknown mask cells
alone cannot qualify. Impacts have radius 0.4 cells and strength 0.35 times the
water style's ripple strength.

`WaterRainIntegration.IsActive(World)` suppresses primitive water rings only for that
world/map's initialized styled-water frame. Classic, zero-strength styles and
uninitialized fields keep the existing fallback. It clears cached primitive
rings when simulation takes over, preventing duplicate impacts. Normal builds
and exports include this opt-in consumer; no `GuoLabs` flag is required.

## Dependency doctor and smoke

No additional software, services, models or artwork is required beyond the
repository's pinned Godot .NET engine and SDK. Run:

```text
dotnet build godot/GUO/GUO.csproj -c Debug
godot-console --headless --path godot/GUO res://src/Render/WaterRain/WaterRainProbe.tscn
```

Expected: `[water rain] PASS`. Use `tools/worktree_setup` if engine/upstream
paths do not resolve. The ordinary smoke launcher includes this contract.

For a Labs-only synthetic GPU proof (supporting evidence, not a shipping clip)
using all five production water models and the real wave field (no shard or client artwork):

```text
godot-console --path godot/GUO --rendering-method forward_plus res://src/Labs/WaterRain/WaterRainVisualProbe.tscn
```

Repeat with `gl_compatibility`. Set `GUO_WATER_RAIN_PROOF` to a PNG filename under
the evidence run to save the comparison: left calm, right rain; rows are UO,
pixel, cel, realistic and classicplus. Headless mode deliberately fails this
pixel test. Live integration and occlusion acceptance are separate:
[client.water.rain](../../../../../tools/scenarios/client/water_rain.md).
