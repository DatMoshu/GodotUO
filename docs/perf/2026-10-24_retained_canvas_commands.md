# Retained ordinary-canvas commands — 2026-10-24

An opt-in experiment removes repeated native submission of unchanged drawing
commands. The ordinary renderer still gathers and sorts the entire current
scene, resolves roofs, transparency, picking and lighting, and updates its
resources. Only the exact ordered native command stream is retained per
pooled canvas item. This is independent of the RenderingDevice Labs prototype.

Set `GUO_RETAIN_CANVAS=1` in the test client's environment to enable it. The
default remains off while live-scene and lifecycle coverage is reviewed. See
[ADR-0002](../architecture/ADR-0002-batcher-on-godot-canvas.md) and
[diagnostic counter definitions](../data_formats.md#41-perf-probe-and-perf-lab-reports-buildperf-toolsperf_probe-toolsperf_lab).

## Why this seam

The preceding actual-Release managed samples placed approximately 34–35% of
main-thread exclusive sample weight at native rectangle submission and
12–14% at native mesh submission. Those samples include native boundaries and
waits; they are not exact CPU or GPU timers. Avoiding unchanged submissions is
a bounded experiment before choosing a C++ interface or custom engine change.

Records compare consumed arguments bit-for-bit, including RIDs, positions,
UV rectangles, colors and transforms. There is no hash-only identity. A changed
or shorter stream clears and replays the exact prefix and current suffix;
unsupported commands bypass retention. Hiding unused pool items prevents
stale geometry after a shorter frame. The cache permits at most 8,192 records
per item and 65,536 reserved records per batcher, falling back without dropping
draws when full. Item state and resource contents remain live.

## First actual Release timing pass — shared load

Same executable and GUO DLL for both modes. DLL SHA-256:
`a62a25b5bb7106f9cb53ce22e2f85d493591b2b1ee7bcd373b699cb41c1b2d8d`.
The engine and Godot C# API both report actual optimized Release. No CUO run.
2560×1440 world viewport, zoom 2, cap 250, PostFX off, classic lighting,
same profile, live world, 15-second settling followed by 30-second sampling.
Order was bank off/on, then docks on/off. Values below use total drawn frames
divided by actual elapsed sample time, not a single FPS overlay reading.

| Scene | Retention off FPS | Retention on FPS | Off/on host CPU maximum |
|---|---:|---:|---:|
| Bank, 1434 1699 0 | 78.384 | 99.647 | 99.4% / 95.4% |
| Docks, 1497 1790 -5 | 120.239 | 138.473 | 99.1% / 83.8% |

These observations favor retention, but host load differed materially and
both runs fluctuated. They do **not** establish a causal 27% or 15% gain,
clean-machine performance, or the 250 FPS target. The bank's one-second ranges
were 58–102 FPS off and 69–124 on; docks were 84–173 off and 95–185 on.

Mean profiler phases across the observations were bank prepare/draw
5.318/3.877 ms off and 4.405/2.925 ms on; docks 3.692/2.344 ms off and
3.353/1.736 ms on. These phase values average rolling profiler windows and
are also affected by contention. The patch does not optimize scene gathering.

| Retention-on counters per drawn frame | Bank | Docks |
|---|---:|---:|
| Records compared | 13,956.9 | 9,370.0 |
| Matching-prefix records | 13,828.2 | 9,340.3 |
| Records submitted, including replay | 144.7 | 49.8 |
| Item rewrites | 0.3 | 0.1 |
| Items bypassing retention | 3.1 | 1.0 |

A matching prefix can be replayed later; its count alone is not avoided calls.
Affine records each make three native calls. Unsupported commands, pool
visibility calls and ordinary clears are outside submitted-record counts.
These are submission diagnostics, not GPU draw-call reductions.

## Validation and limitations

Initial optimized Debug and actual Release suites passed 559 checks in each
mode, including 51 new stream and engine-pixel checks. The warm command-only
test performed 1,000 iterations with no managed allocation or native calls.
A separate read-only source review found no concrete defect and requested
stronger mesh/resource and RD-transition coverage. The expanded suite passed
573 actual Release checks and 912 optimized Debug/Labs checks, including 65
retention checks and 339 RD checks. These exercise same-RID mesh geometry,
bounds and alpha updates without resubmission, empty surfaces, hidden
mesh/texture disposal and replacement, raw triangle runs after matching
prefixes, custom blends, and a retained long/short frame crossing into RD and
back. Full live movement, roofs,
CoT/picking combinations and device acceptance are not established by a
stationary capture suite.

The first separate-login frozen captures matched forest and docks exactly.
Bank differed in 2,599 server-message HUD pixels; dungeon differed in 8,911
stable pixels localized around the player/light, with another 246 pixels not
stable across that run. These raw differences were retained and inspected,
not silently masked. A second diagnostic, `GUO_PERF_PARITY_CANVAS=1`, now
alternates retention off/on/off/on/off in **one client**. On the expanded
Release build (`c9ae4be02af57cdfec92b97e3462eb81b3b713c93d23deab6d70a2dd0a7a46f4`),
all four scenes had **3,686,400 stable pixels and zero differences**, including
dungeon. Login also matched all 307,200 pixels. This establishes stationary
same-client parity, not a claim that all separate-login state is deterministic.

The direct probe also captured 360-frame timing windows. These occurred under
shared load and alongside repository smoke/build work; they are **not clean
performance acceptance runs**. Preserve contrary observations:

| Scene | Off / on mean ms | Off / on allocation KB per frame |
|---|---:|---:|
| Bank | 19.708 / 11.661 | 19.6 / 33.5 |
| Forest | 8.453 / 9.517 | 20.5 / 21.3 |
| Dungeon | 5.338 / 5.178 | 22.2 / 23.2 |
| Docks | 11.147 / 11.618 | 22.6 / 28.1 |

Warm command-only allocation is zero; whole-client allocation in these short
windows is not. Separate cache warming/growth and the existing cover-candidate
allocation from steady-state allocation before promotion. The earlier
separate-login static windows were also mixed: off/on means were bank
16.540/8.845, forest 7.063/5.021, dungeon 4.650/6.265 and docks 10.403/10.037 ms.
The 30-second live samples above are the primary throughput observations.
Full repository smoke, four sampler-driver tests, docs lint, launcher lint
and privacy scan passed.

## Follow-up: steady allocation observations

A read-only allocation review identified different warm-up histories in the
short parity probe's first and second rows. Image allocations occur before
the timing allocation baseline, so they cannot simply be subtracted from
the measured bytes. The whole-process counter also includes profiler,
network, update and probe work. No causal retention regression was identified.

The subsequent frozen Release build adds sustained approximate process
allocation and GC deltas, with no renderer changes from the tested retention
implementation. DLL SHA-256:
`a2530e68d8010d3c5731d99289f3347d833b9ca926a6efaecf0cdb98a1af5078`.
Same live-world settings and settling/sample duration as above, no parity
captures before timing. Order: bank/docks off, then bank/docks on.

| Scene | Off / on FPS | Off / on bytes per draw, approximate | Off / on CPU maximum |
|---|---:|---:|---:|
| Bank | 68.442 / 97.062 | 20,805 / 20,409 | 98.7% / 85.4% |
| Docks | 125.394 / 132.195 | 19,767 / 19,746 | 97.7% / 96.7% |

These windows did not reproduce the earlier large per-frame allocation
difference. They do not establish its cause or eliminate a cold-cache cost.
Higher throughput means more bytes per second: bank approximately 1.424 MB/s
off versus 1.981 MB/s on; docks 2.479 versus 2.610 MB/s. Off windows each had
one generation-0 and one generation-1 collection; on windows each had two
generation-0 and one generation-1 collection. None had a generation-2
collection. Approximate counters include diagnostic allocations.

All runs were under shared load, especially the bank off run, so the FPS
difference is still not a causal improvement estimate. Next allocation work
should attribute actual same-window candidate builds, array growth and mesh
rebuilds before changing cache lifetimes or invalidation rules. Warm command
cache calls remain allocation-free in the separate synchronous test.

Private evidence run:
`20261024_guo-codex-perf-implementation-20261022_retained-canvas`.
Keep screenshots, UO artwork, credentials, client profiles and private logs
out of the repository. This experiment remains local and unpromoted pending
review. It does not change the conclusion that the preceding cover-candidate
cache had mixed performance and higher allocations.
