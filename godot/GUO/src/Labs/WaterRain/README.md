# Rain ripples on styled water (L4, UO+, non-parity)

Owner-requested water follow-up, 2026-10-21. This experiment reuses the
existing **Rain rings on water** checkbox in `-uopweather` / Modern Options
**General > Labs: UO+ weather**. It defaults off for each World/session.
Select a styled water mode (for example `--water-style uo`) to see rain
disturb its wave field. Classic water keeps the weather experiment's
independent pixel-ring fallback. Puddles and wet-player effects stay independent.

The shared input is `WeatherLab.RainIntensity(World)`: 0..1, rain or brewing
weather count divided by 70, zero for dry/snow/missing weather. Effect toggles
gate consumers rather than the signal. The weather seat owns this provider.

Rain stamps run once inside `WaterRipples.Update`, after the field follows the
player and before its existing simulation/upload. No second wave update or
texture is added. The field steps at its existing 20 Hz. Disabling water drops
or ending rain stops new impacts; existing waves decay naturally alongside
boat/mobile ripples. Map/style resets discard fractional rain credit, and the
first frame or a teleport cannot generate an arrival burst.

At full intensity, up to 40 candidate impacts/second are distributed within
18 cells of the player. The weather drop budget also applies, capped at eight
attempts/frame. Delta is capped at 250 ms. Dry/covered/unknown attempts consume
credit, so travelling onto water never releases a backlog. Each candidate
inspects at most 128 loaded tile objects (1024/frame maximum), accepts the
highest Wet land/static only if its height matches the current water mask,
and rejects higher ground, roof/surface statics and multis. Unknown mask cells
alone cannot qualify. Impacts have radius 0.4 cells and strength 0.35 times the
water style's ripple strength.

`WaterRainLab.IsActive(World)` suppresses primitive water rings only for that
world/map's initialized styled-water frame. Classic, zero-strength styles and
uninitialized fields keep the existing fallback. It clears cached primitive
rings when simulation takes over, preventing duplicate impacts. Default builds
and every export exclude this feature and all three guarded simulation seams.

## Dependency doctor and smoke

No additional software, services, models or artwork is required beyond the
repository's pinned Godot .NET engine and SDK. Run:

```text
dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true
godot-console --headless --path godot/GUO res://src/Labs/WaterRain/WaterRainProbe.tscn
```

Expected: `[water rain] PASS`. If the scene class or weather command is missing,
rerun this doctor in Debug with `GuoLabs=true`; use `tools/worktree_setup` if the
engine/upstream paths do not resolve. Set `GuoLabs=true` before running
`launchers/dev/smoke.bat` or its `.sh` pair to include both rain/weather contracts.

For a synthetic GPU proof using all five production water models and the real
wave field (no shard or client artwork):

```text
godot-console --path godot/GUO --rendering-method forward_plus res://src/Labs/WaterRain/WaterRainVisualProbe.tscn
```

Repeat with `gl_compatibility`. Set `GUO_WATER_RAIN_PROOF` to a PNG filename under
the evidence run to save the comparison: left calm, right rain; rows are UO,
pixel, cel, realistic and classicplus. Headless mode deliberately fails this
pixel test. Live integration and occlusion acceptance are separate:
[client.water.rain](../../../../../tools/scenarios/client/water_rain.md).
