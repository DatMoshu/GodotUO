# Cross-launch pixel variance: two causes fixed by fixture, one open bug (PERF-LANE-01)

Two launches of the **same** Release export, photographed in the frozen-world
scenes, did not match pixel for pixel. Parity between two builds can only be
claimed when the difference between two launches of one build is zero, so
every "N pixels differ" result of 2026-10-23/24 carried this noise.

## What differed

From the lazy-land checkpoint (`2026-10-24_lazy_land_buckets.md`, added with the lazy-land change in PR 154), 2560x1440,
zoom 2, five-frame stable-pixel comparison:

| Scene | exact parent vs candidate | candidate vs earlier capture | where |
|---|---:|---:|---|
| login | 0 | 0 | |
| britain-bank | 11,394 | 5,731 | earlier-capture difference confined to the bottom-left server-message lines |
| dense-forest | 285 | 0 | sparse terrain pixels |
| dungeon | 90 | 0 | sparse terrain pixels |
| britain-docks | 706 | 3,695 | earlier-capture difference confined to the bottom-left server-message lines |

The native-profile control (unchanged disabled build, repeated) showed
12,256 raw / 11,344 stable pixels at the bank, including visible rain and
system text; the native-v3 pair showed 261 raw pixels at the docks on the
centre actor and 0 stable differences elsewhere.

## Causes

1. **Rain.** The shard's weather packet starts rain on its own schedule;
   `--perf-static-world` removes mobiles, items and effects, not weather.
2. **Server and system text.** The welcome lines, the "it begins to rain"
   line and the overhead echo of the probe's own `[go` command land in the
   capture, at times that differ by launch.
3. **A terrain variant (open bug).** Sparse terrain pixels in the forest,
   dungeon and docks change by palette-sized steps, not by +/-1 channel
   rounding. Two variants repeat across launches of an unchanged build. An
   atlas-placement or UV-rounding cause is a hypothesis only: unproven, and
   no shader or sampling change was made for it.

## What changed

Causes 1 and 2 are removed by the quiet fixture: `--perf-quiet` on the probe
(and `GUO_PERF_QUIET=1` / `tools/perf_dump --quiet`) ignores the weather
packet, resets any weather already running, and drops every incoming message
while the probe runs (`GUO.Host.PerfFixture`, two marked seams in
`PacketHandlers` and `MessageManager`; refused without `--perf-probe`). The
count gate always passes it and refuses a report without `quiet_fixture`.

Cause 3 stays open. Parity claims made with the quiet fixture must still
report the raw difference of an unchanged-build control pair taken in the same
runtime window, and must not repeat captures until a pair happens to match.

## Evidence

The comparison receipts (`lazy-land-parent-comparison`, the earlier-capture
comparison, the pixel variation profile and the comparison driver) are kept
with the lane's private run evidence, not in the repository; the PNGs stay in
the lane's build folder.

## Next step for the terrain variant

Take two quiet-fixture launches of one export in one runtime window, save the
five frames per scene, and for each differing pixel record the atlas page,
source rectangle and sprite under it (the batcher already knows these at draw
time). If the pixels sit on a sprite whose atlas placement differs between the
launches, the variant is load-order dependent, not a rendering bug.
