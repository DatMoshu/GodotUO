# Runtime and atlas attribution — 2026-10-24

This change measures the remaining work; it does not change rendering, atlas
packing, upload scheduling, game updates, or the default renderer. It follows
the gather-input snapshot change (`72fd49fb`) and the whole-client Fable review.
All timings below are GUO-only actual Windows Release, under shared-host load.
No isolated speedup or 250 FPS acceptance is claimed.

## Measurement

The optional `GUO_PERF_RUNTIME_ATTRIBUTION=1` probe records main-thread update
and draw wall times, atlas creation/update counts, actual page dimensions,
submitted RGBA byte counts, and synchronous call wall times. Atlas owners are
art, land, animation, glyph, gump, light, and texmap. Reports distinguish these
uploads from the existing land texture-array counters. Ordinary gameplay leaves
these timers disabled. The public two-argument TextureAtlas constructor remains.

World samples discard 60 seconds after arrival, then measure 30 seconds standing
or 60 seconds walking. The login row keeps its short 360-frame measurement.
The window is 2560×1440, zoom 2, Forward+, plain profile, PostFX off, live world.
The probe uncaps both engine and client pacing during measurement. Retained
canvas, GPU-world and the RD prototype are off. Routes and GC collection deltas
are recorded. Walking turns when blocked, so these are observed routes, not a
deterministic bank-to-docks replay.

Frozen Release DLL SHA-256:
`b521f62049c6383de017362649d1f6c31ebd7e19c884e5a86ca65d809568af1b`.
Both the engine and GodotSharp identity checks confirm Release. Final v2 DLL,
after the review fixes and additional checks:
`f8ae0f8366356a7ad8f73431c69abe3be73a783fdb4eeb123b3e58d61adf4b7e`.
The evidence
includes before/after executable identity and owned-client cleanup records.

## First sample: bank, docks, then bank-origin walk

| Scene | Seconds | Draw FPS | Mean ms | p95 ms | p99 ms | Max ms | Prepare ms | World draw ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Bank | 30.011 | 77.10 | 12.969 | 19.509 | 22.721 | 27.619 | 5.182 | 4.042 |
| Docks | 30.012 | 94.16 | 10.620 | 16.386 | 19.316 | 25.049 | 4.052 | 3.165 |
| Walk from bank | 60.010 | 51.67 | 19.352 | 27.686 | 31.229 | 81.158 | 7.406 | 6.264 |

Host CPU reached 100% across the run, including startup and warm-up. These
values are not comparable as a before/after speedup against earlier samples.
The 60-second walk observed 185 tiles, dropped no route entries, and did not
grow the probe's preallocated sample storage.

The bank's Process callback averaged 9.878 ms, including 9.790 ms in Draw.
Packets averaged 0.016 ms, SceneUpdate 0.050 ms, UI update 0.017 ms and audio
0.002 ms. Docks Process averaged 7.765 ms, including 7.690 ms in Draw.
Thus this run does not support a hidden multi-millisecond packet/UI/audio
update cost. These are wall times including host descheduling; the gap to
whole-frame time includes engine work, waits, and other callbacks. It is not
an exclusive CPU or GPU attribution. Nested times must not be added together.

| Interval | Atlas updates | New pages | Submitted RGBA bytes | Upload-call wall time |
|---|---:|---:|---:|---:|
| Bank | 13 animation | 0 | 218,103,808 | 32.928 ms |
| Docks | 10 animation + 1 glyph | 0 | 184,549,376 | 22.796 ms |
| Bank-origin walk | 110 art + 32 animation + 4 land + 1 texmap | 0 | 2,466,250,752 | 379.954 ms |

The walk had uploads in 143 measured intervals, at most two per interval.
Upload-containing intervals plus their immediate successors numbered 268,
averaged 21.527 ms, and peaked at 41.495 ms. The worst frame (81.158 ms) was
outside that group. Uploads matter for travel, but this does not identify them
as the cause of the worst hitch. Submitted byte counts describe full RGBA
images passed to the API, not measured GPU traffic or completion times.

Bank and docks each collected Gen0 once, with no Gen1/Gen2 collection. The
walk collected Gen0 five times and Gen1 three times, no Gen2; reported average
GC pause was 0.037 ms/frame. Allocation was approximately 19.6 KB/frame
standing and 70.6 KB/frame walking, including diagnostic overhead. New atlas
observation records can allocate during a measurement; clean repeated flushes
do not allocate for profiling.

A separate attempted docks-origin walk at 1497,1790,-5 remained on exactly one
tile for 60 seconds. The harness rejected it for zero movement; its timing is
not accepted as a travel sample. The diagnostic docks origin now reuses the
existing pier fixture, avoiding a second conflicting coordinate definition.

The corrected v2 pier-origin walk moved 179 observed tiles in 60.000 seconds,
with no dropped route entries or sample-storage growth. It recorded 73.72 draw
FPS, mean 13.566 ms, p95 19.901 ms, p99 22.635 ms, and max 38.600 ms. Host CPU
reached 99.8%. Its different route and load make it unsuitable for a before/after
comparison with the bank walk. There were 162 page updates (86 art, 69 animation,
4 texmap, 3 land), no creations, and 362.729 ms summed upload-call wall time.
Preparation/drawing averaged 5.146/4.234 ms. The 289 upload-associated intervals
averaged 15.745 ms and peaked at 27.901 ms, again below the run's worst frame.
GC counts were 3/2/0 for Gen0/1/2 (inclusive generations), with reported mean
pause 0.010 ms/frame and approximately 35.9 KB/frame allocated.

## Next decisions

World gathering and sprite submission remain the largest measured targets.
Continue incremental static/cover preparation and test a contiguous sprite-run
bulk-submission path separately behind its toggle. Preserve sorted order,
nearest sampling, roof/CoT behavior, hue/alpha, lighting and picking.

Atlas work should target an observed owner or mutable region. A blanket upload
cap can delay newly visible pixels and is not a parity-neutral optimization.
The minimap was closed in these samples, so they do not test its known mutable
gump-page behavior. Dedicated minimap storage needs its own open-minimap walk
and pixel proof. Smaller atlas pages need measurements of added pages, texture
switches and creation costs as well as upload time.

The general Windows export tool still defaults to ExportDebug, and the existing
count gate accepts an optimized assembly without requiring a Release engine.
These samples use a separate explicit ExportRelease harness with runtime identity
checks. Adding the public Release option and strict identity gate remains a
separate tooling change; the present diagnostic work does not claim to fix it.

## Validation and limits

The final actual Release export passed 594 batcher checks, including 21 atlas
accounting, runtime-scope, route and exception-cleanup checks. New cases cover
dirty-at-start pages, coalesced writes, normal-page rollover, populated oversized
creation, disposed history, interval reset, byte totals, passive final movement,
route overflow, and restoring frame pacing/timers after a synthetic exception.

The independent source review found a creation-call timing boundary that included
List insertion; the final source measures only CreateFromImage. It also identified
older probe cleanup and final-movement gaps. Measurements now restore engine/client
pacing, VSync and diagnostic flags on success or exception; outer probe cleanup
restores profiler and login touch/pointer state. Final route observations count
distance without issuing an extra move. Report versioning distinguishes this
route accounting from earlier evidence. No new renderer mode is promoted.

Four-scene same-client frozen parity passed. Cross-build stable captures against
the prior gather build matched all 3,686,400 pixels at forest, dungeon and docks.
Bank differed in 2,599 pixels, entirely inside x=2..280, y=1306..1337: the prior
capture had the server's town-guard departure message, absent in the new capture.
The saved crop was visually inspected; world pixels matched. Raw differences and
the failing whole-frame assertion remain in evidence rather than being hidden.

The optimized Debug/Labs compatibility run passed 933 checks, including the
existing 339 ordered RenderingDevice checks. These are compatibility results,
separate from actual Release timings.

The required full smoke command returned exit 0 and `OK`. It still contains
the previously reported editor Art advisory: 100/101 with `splat_lods_present`
failed despite the aggregate success. This predates the attribution change;
do not describe every editor subcheck as passing. Docs lint (318 documents),
privacy scan and whitespace checks passed.

Private run: `20261024_guo-codex-perf-implementation-20261022_runtime-attribution`.
Numeric reports, host-load samples, reproduction scripts and review findings
stay with that run. No credentials, profiles, raw traces or game artwork are
part of this source report.
