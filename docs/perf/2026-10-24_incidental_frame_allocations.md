# Incidental frame allocations

This bounded ordinary-renderer change follows the retained-travel allocation
profile. It changes no renderer default or GPU prototype. No CUO run is needed.

## Source change

`Control.AddToRenderLists` previously entered `DrawDebug` for every control.
The compiler creates that method's captured-argument object before its internal
visibility/debug guard. Repeat the existing guard at the call boundary so normal
play never enters the allocating method. The debug callback itself is unchanged,
including when it reads live control dimensions.

`CoverRuns.NextFrame` previously allocated a predicate per hash bucket and a
temporary list every 60 frames. Keep one instance predicate and reuse the list.
The predicate reads the current frame on every call; the original strict age
comparison, 60-frame cadence, entry order and removal behavior remain intact.
Storage can still grow when an expiry sees more empty buckets than before.

These are allocation reductions, not a renderer rewrite or an assumed FPS gain.
The preceding bank/docks allocation traces attributed 88.3%/89.1% of weighted
sampled bytes to UI drawing and 4.0%/3.8% to cover expiry. Those percentages are
allocation weights, not CPU-time shares; only part of the UI allocation is
addressed here.

## Validation and measurement contract

The optimized Debug and actual exported Release probes pass 798 checks.
Four added checks cover 1,000 normal-control calls without allocation, invisible
debug controls without allocation, preservation of the visible debug callback,
and ten idle cover-expiry periods without allocation after warmup.

The before build is the hardened retained-travel diagnostic build, SHA-256
`0f030713374db96a8bae1bcbe1f29fc393fe90bde3f7837c86f31ec6313aeaf7`.
The after build is SHA-256
`71ef23dcf2bb5014b41150f5c60ef2f0db7080b231de1d978acd0b16a5ee30fe`.
Both are frozen actual Release exports. Retained canvas, both GPU experiments,
and PostFX remain off. Live timing uses 2560×1440, zoom 2, 60 seconds discarded
at the location, then 30 seconds of actual draw intervals with cap/vsync disabled.
The bank fixture is `[go 1434 1699 0`; docks is `[go 1499 1761 -3`.

Other projects remain active. Host load is recorded; this is not an isolated
benchmark. Bank runs before/after, docks after/before. Pixel captures are separate
static-world fixtures, not the timed live world. Allocation tracing also runs
separately from timing.

## Sustained observations

| Scene | Before / after FPS | Before / after mean ms | Before / after p95 / p99 ms | Before / after KB allocated per frame |
|---|---:|---:|---|---:|
| Bank | 61.778 / 79.899 | 16.187 / 12.516 | 22.549 / 25.895; 17.801 / 19.504 | 19.6 / 14.1 |
| Docks | 84.826 / 96.174 | 11.789 / 10.398 | 17.106 / 18.345; 15.257 / 17.115 | 19.6 / 14.0 |

Bank recorded 1,854 / 2,397 draws over 30.011 / 30.000 seconds. Docks recorded
2,545 / 2,886 draws over 30.002 / 30.008 seconds. Main-thread allocation totals
were bank 37,224,896 / 34,589,736 bytes and docks 51,205,216 / 41,398,016 bytes;
the after totals cover more frames. Allocation per frame fell about 28% in these
observations, consistent with removing the targeted sites. The separate trace
follow-up below confirms that those sites disappeared from allocation samples.

Host CPU peaks across each complete run, including startup/warmup, were bank
100.0% / 96.0% and docks 95.9% / 100.0%. Their means were 55.0% / 46.6% and
45.9% / 48.9%. Atlas page updates during timing were bank 5 / 3 and docks 13 / 2.
These are unequal workloads and shared load: the FPS increases are observations,
not isolated speedup estimates. No general 250 FPS result is established.

## Pixel check status

Both exports passed the same-client plain/batched check with zero differences
in all four world fixtures. Cross-build plain captures matched at login, but
bank/forest/dungeon/docks differed in 11,193 / 1,294 / 92 / 922 pixels. Some bank
and forest differences are server-message text; others follow terrain edges.
An unchanged-baseline repeat reproduced every terrain-edge difference at the
same pixels. That repeat matches the patched bank, dungeon and docks captures
byte for byte. Forest differs only in 858 pixels inside the inspected system
message "It begins to rain." at x=2..129, y=1386..1400; every pixel outside that
message matches. The cause of baseline launch-to-launch terrain variation is
not established, but this control demonstrates it without the allocation patch.
All raw differences and the unchanged-build control are retained.

Every same-client capture recorded two actual client draws before readback;
all 3,686,400 world pixels were stable within each capture sequence. These checks
cover the captured stationary scenes, not every gameplay or device combination.
Final validation passes 798 actual Release and 1,137 optimized Debug/Labs checks.
The normal full smoke exits successfully with aggregate `OK`, while its inherited
editor `splat_lods_present` assertion still reports failure. That editor issue is
not fixed or hidden here. Docs lint, 114 launcher-pair checks, privacy scan and
diff whitespace checks pass. All owned sampling clients have cleanup receipts.
This is a local review candidate; no push, merge or experimental default change
is implied.

## Allocation attribution after the change

Separate 30-second `gc-verbose` traces contain 306 bank / 422 docks weighted
allocation samples, totaling 32,620,408 / 44,986,016 sampled bytes. Both have zero
lost events and zero missing allocation stacks. Tracing happened after timing,
with another 30-second settling interval; it is not included in the FPS rows.

The old debug-draw captured-argument type (`<>c__DisplayClass156_0` on the
`Control.AddToRenderLists` stack) accounted for 9,913,800 / 13,211,888 sampled
bytes in the preceding bank/docks traces. It has zero samples after this patch.
`CoverRuns.NextFrame` similarly falls from 1,812,200 / 2,238,600 sampled bytes
to zero. These are weighted samples, not an exact object census or comparable
per-second throughput totals. The zero-allocation microchecks provide the
bounded exact evidence for the disabled-debug/idle-expiry cases.

Some bank samples still have a `Control.AddToRenderLists` frame, but their types
are delegates, gump arrays and another control's captured arguments, not the
removed debug closure. Do not equate an inlined/recursive caller frame with one
allocation source. GumpPic, Button, GumpPicTiled and fresh child RenderLists remain
the leading UI targets. A typed-command follow-up must preserve mixed order,
deferred dimensions and clipping, and measure its increased buffer footprint.

Local evidence run:
`20261024_guo-codex-perf-implementation-20261022_incidental-frame-allocations`.
Raw captures, profiles and traces are not for publication.
