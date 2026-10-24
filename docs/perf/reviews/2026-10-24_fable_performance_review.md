# GUO performance review for Fable — 24 October 2026

Owner: Moshu. Implementation/review: Codex, continuing Fable's 22 October
handoff. This consolidates the rendering/performance lane through the local
incremental-cover, retained-canvas and gather-input work, followed by runtime
and atlas attribution, scoped cover-cache retention, allocation fixes and
deferred retained visibility. It distinguishes ordinary-path Release work from the
off-by-default GPU experiment. It is a review record, not approval to merge or
enable either GPU mode.

## Current assessment

GUO now avoids substantial repeated work, with explicit lifetime and pixel
checks. Bank and docks still have expensive CPU preparation and submission.
There is no established general 250 FPS result at 2560×1440, zoom 2. That target
requires the entire frame to fit within 4 ms. Callback timings, profiler CPU
scopes and reduced draw counts cannot independently establish it.

Latest completed ordinary Release observations are in
[cover membership validation](../2026-10-24_cover_membership.md): bank standing
72.513 FPS and docks 117.449 under unequal shared load. Walking before/after is
69.147/70.602 at bank and 97.999/95.762 at docks; preparation and p95 slightly
worsen despite about 76%/80% fewer covering-plan rebuilds per draw. These are
not isolated speedup estimates. Exact world sorting and typed UI commands are
also completed local candidates. The small [lazy land-bucket candidate](../2026-10-24_lazy_land_buckets.md)
saves 64 bytes per fresh list and reduces live allocation to 5.1/5.0 KB/frame.
Its loaded bank/docks FPS observations worsen (90.392→83.845 and
102.995→96.301); no speedup is established. Private native attribution is
being documented separately from these ordinary Release measurements.

Earlier retained-canvas observations are in
[deferred retained visibility](../2026-10-24_deferred_retained_visibility.md).
The off-default retained experiment eliminates almost all of its roughly
3,300 native hide/reveal calls per stationary frame. Previous retention versus
deferred retention measured bank 74.292→102.272 FPS and docks 90.789→98.302,
under unequal shared load and different command/upload activity. Candidate
retention-off measured 83.022 at bank and 98.347 at docks: the docks whole-frame
result is effectively unchanged against ordinary Release. These are observations,
not isolated gains. Four direct off/on world captures match all pixels; 940
Release and 1,280 optimized Debug/Labs checks pass. Exact 46-tile routes measured
bank 60.051→69.401 FPS and docks 90.276→91.461 under shared load, with unequal
atlas activity. Travel visibility calls fell from roughly 3,170 per draw to
less than 0.46; docks p95 slightly worsened. Full evidence and limits are in the
linked report. The toggle remains off by default.

The preceding [incidental allocation fix](../2026-10-24_incidental_frame_allocations.md)
reduced bank/docks from 19.6 KB/frame to 14.1/14.0 in actual Release and removed
the targeted debug/expiry allocation sites from fresh sampled traces. Neither
these allocation results nor the reduced visibility counts imply a 4 ms frame.

Earlier sustained observations are in [cover-cache retention](../2026-10-24_cover_cache_admission.md):
bank 80.721→85.601 FPS, pier docks 98.466→93.213, bank walk 63.869→70.995,
and pier walk 75.669→79.559. These are actual Release, 2560×1440, zoom 2,
60-second discard, 30-second standing / 60-second walking samples under
95–100% peak shared-host CPU load. **Mixed FPS; no isolated speedup claim.**
The stronger finding is reduced travel rediscovery/allocation: bank cover builds
75,090→22,988 and 60.9→49.1 KB/frame; identical observed 180-tile pier route
35,403→4,950 builds and 34.8→30.5 KB/frame. All raw limits and contrary results
are retained. Both GPU modes and ordinary canvas retention were off.

The cache keeps hard map-table/lifetime invalidation and broad chunk membership
checks. It only spares unaffected resident plans from ordinary slot churn and
skips retention bookkeeping for full-cache misses. Live raw-chain checks covered
134.1 million references with zero mismatches. Direct cached/live pixel checks
now record actual client redraws and cache hits per capture: engine post-draw
signals alone were not sufficient proof. See the report for exact build identities,
tests, old cross-build differences and the strengthened capture checks. Final
validation passed 700 Release and 1,039 optimized Debug/Labs checks plus the
four-scene direct pixel comparison. Full smoke returned aggregate OK with the
inherited editor `splat_lods_present` failure still present.

The owner has changed the workflow: **stop rerunning CUO cohorts**. Make bounded
GUO changes, load the fixed locations and record sustained GUO FPS and phase
timings. Keep all attempts and label shared host load. The gather-input Release
comparison discards 60 seconds and records 30 seconds per location: bank
71.285→74.058 FPS, docks 121.416→123.827 FPS. Competing load reached 96–100%,
so these small changes do not establish a throughput gain. Keep the local
candidates under review. The following attribution runs diagnose costs rather
than claiming another optimization.

## Work completed, in dependency order

| Commit / lane | Change | What it removes or fixes | Boundary |
|---|---|---|---|
| Fable integration through `ce97ed19`, PR 130 | Numeric depth sorting, cover stamps, shared StringNames, pooled canvas state, lighting-list traversal | Repeated sorting/interop/allocation work | Inherited; see original handoff |
| `4c012423` | Water smoke path normalization | False smoke failure; 64 checks passed | Tool correction, not an FPS change |
| `74b3eaca` | Lazy RenderLists key buffer | Eager 8 KB allocation on UI-created lists | Login 55→7.2 KB/frame; world 77→20.5 KB/frame in Fable's cohort |
| `e753b372` | Restore complete realtime light receiver geometry | Lighting regression from incomplete receiver input | Correctness first; lighting workload must remain complete |
| `06162827` | Runtime identity and transparent-scene settling | Misleading build labels and mid-fade samples | Measurement infrastructure |
| `93f9ee14` | Persistent ordered terrain; dirty chunk slots | Rebuilding unchanged terrain geometry | Ordinary renderer; preserves draw order |
| `627928d4` | Ordered RenderingDevice prototype | Establishes a direct GPU submission path | ADR-0043, L4, Debug/Labs only |
| `c80abcec` | Defer ordinary canvas work until fallback | Duplicate native canvas primitives while RD succeeds | Exact early-prefix/late-frame replay retained |
| `8a552b9b` | Match CUO raster origin | Black tile/terrain seam regression | Coordinate/raster fix, not texture filtering |
| `70a7fc4b` | GPU-resident geometry and dirty ranges | Reuploading unchanged world quad geometry | Separate ordered occurrence IDs retain overlaps |
| `6069cb43` | Aligned probe windows; retain every parity frame | Ambiguous timing/capture windows | Diagnostic correction |
| `6b14a161` | Clear only prior active canvas items in RD mode | Reclearing an unused pooled tail | Toggle-off keeps ordinary behavior |
| `9aad2464` | Exact per-occurrence transform records | Repeated geometry variants for transform state | Original transform bytes and ordering retained |
| `e334dbb7` | Four original textures per ordered RD batch | Extra texture-bound draw submissions | No texture sorting, atlas repack or filtering |
| `239bc8e9`, PR 149 | Cheaper exact RD vertex packing | Godot value/helper overhead in CPU capture | Independent byte oracle, including signed zero/nonfinite cases |
| `b35a00f9`, PR 151 | Cache retained covering-mesh RIDs | Native GetRid on each reused covering run | Actual Release; refresh after every mesh build |
| `333e3e43`, draft PR 152 | Cache registered land-page dimensions | Per-tile native width/height queries | Standalone/resizable textures remain live |
| `7089290d`, draft PR 153 | Persistent source records and revisions | Repacking and hashing unchanged source vertices | GPU prototype only; multiple revisions may coexist |
| `496bedd8`, local | Atlas-owned dimensions and RID at batcher boundary | Repeated A/B/A native measurements | Actual Release; registry removed before atlas disposal |
| `17147cfd`, local | First-Land identity cache | Repeated head/TNext scans per cover query | Mutations invalidate ownership-certified cells |
| `393c50e8`, local | Immutable packet references through capture/replay | Copying four cached vertices per occurrence and expanding replay vertices | GPU prototype only; ticket lifetime and packet recycling tested |
| `ea594411`, local | Incremental covering-land candidate discovery | Repeating neighboring tile/chunk discovery for static objects | Live alpha/depth/overlap/stamps, roofs, lights and picking remain authoritative; no measured gain established |
| `b2fa6237`, local | Retain ordinary canvas commands | Resubmitting identical native draw commands | Off-default experiment; exact stream comparison and fallback replay |
| `72fd49fb`, local | Snapshot shared values during one synchronous gather | Repeated profile/player/CoT input reads | Object decisions stay live; timings and runtime invariants recorded separately |
| `a64106c3`, local | Optional update/draw scopes, atlas upload accounting and timed warm-up/walk diagnostics | Makes hidden update cost and travel uploads measurable | Actual Release, shared-load observations; no performance gain claimed |
| `33e39408`, local | Per-entry missing-slot epochs, hard map lifetime epoch and full-cache admission guard | Unnecessary travel-time discovery and rejected-plan bookkeeping | Broad membership checks preserved; shared-load observations and exact same-client cached/live pixels |
| `12ab861c`, local | Exact 46-tile replay, interval counters and allocation attribution | Ambiguous routes and unmeasured native visibility work | Corrected cohorts include contrary retention results; no default promotion |
| `1381cae1`, local | Guard unused debug callbacks; reuse cover-expiry predicate and scratch | Incidental per-frame UI/expiry allocations | Actual Release allocation reduction; ordinary pixels and debug behavior preserved |
| `3ee23a5f`, local | Keep reused retained items visible until a complete frame ends; fix clipping transition | Redundant hide/reveal calls and clear-after-clip regression | Existing off-default experiment; explicit abort recovery and target/clip pixel checks |
| `79a33c35`, local | Ordered typed UI texture commands | Repeated simple texture closures | Deferred dimensions, mixed order and clipping retained; 14.1/14.0 to 5.9/5.8 KB/frame in its cohort |
| `419e7a7a`, local | Adaptive exact world-depth radix sort | General comparison sort for qualifying queues | Integer order oracle; bank/docks sort stage 0.447/0.328 to 0.091/0.082 ms |
| `0c0e8a5b`, local | Revalidate first-Land identity and chain certification | Unnecessary plan rebuilds after benign mutations | Lower rebuild counts, mixed FPS; every mutation hook remains active |
| `d0be5ce1`, local | Allocate two loose Land queues only on first use | Empty lists in short-lived UI owners | 64 bytes saved per fresh list; 1,217 Release / 1,557 optimized Debug/Labs checks; no FPS gain established |

PR references identify the existing stack, not a fresh remote-state audit.
Only the previously authorized pushes through PRs 152/153 were made. The
following local commits and candidate require review; no new push or merge is
implied by this document.

The latest [runtime attribution report](../2026-10-24_runtime_attribution.md)
records bank preparation/drawing at 5.182/4.042 ms and the PerfProbe docks
fixture at 4.052/3.165 ms. Packet/UI/audio updates were small in that run.
The 60-second bank-origin walk made 147 atlas page updates, but its worst
frame was outside upload-associated intervals. Continue gathering/submission
work; do not infer that a blanket upload cap fixes the remaining cost.
The docks fixtures differ between PerfProbe and earlier PerfDump samples;
their coordinates and shared-host-load limitations are explicit in the report.

A follow-up source review narrowed the cover-cache recommendations. Auxiliary
`WorldMapGump.ChangeMap` construction can replace the shared map table while old
chunks remain alive, so all entries still need an unconditional table/lifetime
generation. Ordinary slot churn may be scoped to plans that observed missing
chunks only after preserving that distinction. Land-only membership version
bumps are unsafe: detaching a non-Land anchor or changing a foreign chain can
change the first Land or its ownership certification. Keep existing membership
invalidation until a semantic replacement has an oracle. Capacity admission can
skip retention bookkeeping on a full-cache miss while keeping the live cover
walk and existing hits unchanged. The local cover-cache candidate implements
these bounded changes; its travel observations and limitations are recorded
above. Further membership narrowing remains unimplemented.

## Two GPU experiments must remain distinct

The older `--gpu-world` path (ADR-0007) instances statics with a constructed depth
key. Fable's integrated run had zero fallback/skipped cover tiles but repeatable
differences: roughly 46k bank, 42k forest, 28k dungeon and 78k docks pixels.
Equal-depth ordering between instanced statics and covering land remained open.
It stays off. Do not describe the newer prototype's tests as fixing that path.

The newer `--rd-world` path (ADR-0043) consumes the existing CPU semantic order,
preserves repeated translucent occurrences and renders original art with nearest
sampling, original hues, land normals and blend behavior. Unsupported cases use
canvas replay. It has persistent geometry, source revisions, packet references,
ordered IDs, transform state and four-texture batches. It is still **optimized
GUO Debug/Labs with Debug Godot bindings**, absent from exported Release builds.

The prototype still constructs ordered occurrences/commands and evaluates the
world on the CPU. Dynamic sprites still capture/compare their content. Camera
movement can invalidate ordinary sprite records. Source residency does not mean
zero CPU work, zero allocations, GPU culling, or complete live-gameplay parity.

## Latest GUO-only sustained Release samples

Configuration: actual exported Release engine and optimized Release GodotSharp;
2560×1440 camera bounds, zoom 2, cap 250, classic lighting, PostFX off. Full live
world, not the static-only parity fixture. Each scene settles before roughly
30 seconds of one-second samples. No CUO process is launched. FPS is drawn
frames divided by the measured interval; phase timings remain separate profiler
observations. Other projects are unrestricted.

| Location | Before FPS | Candidate FPS | Before one-second range | Candidate range |
|---|---:|---:|---:|---:|
| Bank `[go 1434 1699 0` | 102.8 | 94.1 | 81.6–123.4 | 63.5–109.7 |
| Docks `[go 1497 1790 -5` | 173.9 | 175.5 | 123.9–196.5 | 131.4–196.9 |

Before: 3,105 drawn frames / 30.200 s at bank; 5,232 / 30.093 s at docks.
Candidate: 2,838 / 30.175 s at bank; 5,279 / 30.081 s at docks.
Host CPU peaks were 100.0% / 86.0% for before / candidate bank, and
77.1% / 86.2% at docks. These are explicitly
**under-load observations**, not causal speedup estimates. The sample writer's
small periodic overhead is present in both variants. The existing 60-frame
profiler ring is not a sustained frame-time distribution or a GPU timer.

The observed first-pair change is bank −8.5%, docks +0.9%. Shared load prevents
causal attribution; it does not turn the slower bank observation into a win.
Mean sampled world-prepare / world-draw scopes were bank 3.834 / 3.165 ms before
and 4.188 / 3.579 ms after; docks 2.215 / 1.673 ms before and 2.120 / 1.658 ms after.

A reverse-order bank repeat (candidate, then baseline) produced **89.5 / 86.8
FPS**: 2,699 frames / 30.149 s versus 2,622 / 30.193 s. One-second ranges were
55.7–112.2 / 62.4–108.6, host CPU peaks 98.7% / 99.2%. Candidate prepare/draw
means were 4.523 / 3.697 ms; baseline 4.884 / 3.548 ms. The observed ratio
reversed to +3.1%. Retain both pairs; no reliable bank speedup is established.

Warm first-Land lookups fell from 48,944 to 15,909 per bank frame and 21,438 to
8,739 at docks. Candidate hits averaged 1,116.8 / 456.0 per frame, with
2,629.9 / 1,088.0 dependency checks. Bank built about 0.2 plans per frame;
docks rounded to zero. Eliminating lookups is established; higher FPS is not.

Baseline DLL: `819a867dd7dea07e3305c9faad324255126b286ee1a653ae942a4e2b4cb1cf2b`.
Candidate DLL: `80b4ae091123267c8356a43b3cdac43bbb2660985b72b1b1001069f0b27dde18`.
Both are separate frozen exports; the baseline includes the same sampling
instrumentation before the candidate-cache change.

## Earlier numbers: retain their original meaning

Fable's integrated defaults probe at 2560×1440, zoom 2 reported these elapsed
frame means, before this continuation:

| Scene | Before integration ms | Integrated ms |
|---|---:|---:|
| Bank | 15.9 | 10.3 |
| Docks | 12.1 | 8.8 |
| Dungeon | 6.8 | 4.4 |
| Forest | 7.7 | 6.1 |
| Walk | 7.5 | 6.6 |

Fable measured about ±8% run-to-run mean noise in that setup. Its build/preset
and sampling contract differ from the new actual Release sustained samples;
do not combine these tables into a single percentage improvement.

The last ordinary Release first-Land cohort was mixed:

| Scene | Prior GUO frame ms | First-Land GUO frame ms | Historical CUO frame ms |
|---|---:|---:|---:|
| Bank | 8.539–11.406 | 9.794–19.503 | 4.070–5.307 |
| Docks | 5.545–5.648 | 5.099–6.211 | 4.074–4.363 |

All were under shared load (host maxima 44.4–88.1%). These 60-entry profiler
windows do not establish a reliable Release FPS gain. The slow bank observation
is retained. Earlier Release covering-handle and atlas cohorts also contain
mixed outcomes; their full tables are in the linked component reports below.

The latest **GPU prototype** packet-reference frame windows were:

| Scene | Earlier prototype ms | Packet-reference prototype ms |
|---|---:|---:|
| Bank | 13.941–13.991 | 13.095–13.180 |
| Docks | 8.250–8.442 | 7.621–9.089 |

Bank was about 6% lower in this small interleaved cohort; docks was mixed.
A separate 360-frame probe showed bank **slower**, 13.978→14.466 ms, and docks
12.833→11.974 ms. Both results are retained. These are Debug/API prototype
observations under load, not Release or a fair shipping CUO ratio.

Warm packet-reference scenes avoided 3,880,320 bytes of cached-source vertex
copies per bank frame and 3,709,056 at docks (references × 192 bytes). These are
logical copies removed, not measured DRAM bandwidth or managed allocation
savings. Warm geometry uploads, new packet allocations and canvas replay were
zero in the measured static windows; overall allocation was still about 20 KB/frame.

## Initial incremental preparation candidate

The cache retains the ordered neighboring Land identities, including repeated
occurrences, after the source object's ground/front, height and width checks.
It applies only to statics/multis. It does not cache visible-object lists,
occlusion winners, alpha, depth, cover stamps, mesh indices, lighting or picking.
Dynamic objects retain the original traversal.

Each dependency records a chunk membership generation. Every existing cell and
whole-chunk invalidation advances it, including direct unlink, XYZ-before-move,
reload and pooled reuse. Cold first-Land scans certify complete chain ownership;
foreign/detached/mixed chains cannot publish retained plans. A shared map-table
epoch covers absent/new/replaced slots and Map lifetimes. Valid hits preserve
the original chunk-access-time side effect.

The cache is lazy on world lists, bounded to 8,192 entries and 262,144 retained
candidate/dependency references, with a 1,024-reference plan limit. Unused plans
expire; scene release/map change drops references. At capacity it preserves warm
entries and skips new admissions rather than clearing/reallocating everything.
This addresses the reviewer's initial capacity-thrashing concern.

That candidate passed **508 batcher checks**, including 50 new cache
checks and the existing 138 first-Land checks. New checks cover ordered duplicate
identities, original live queue predicate/Seq/counters, alpha/depth/NaN/overlap,
zero-allocation warm hits, direct unlink, null insertion, reload, alias rejection,
changed map/shape keys, mutation during discovery, hard limits and expiration.
Source review found no concrete correctness blocker. These checks do not prove
the allocation rate of the whole client or every live gameplay combination.

Frozen actual Release comparison: docks and dungeon are byte-exact across
builds. Bank has 2,599 differing pixels, all in the inspected town-guard message
at x=2..281, y=1306..1338; forest has 4,553, all in inspected rain/world-save
messages at x=2..297, y=1344..1401. Raw differences and crops are retained.
There are no differences outside those text bounds. Same-build terrain on/off
captures are exact across all 3,686,400 stable world-frame pixels in each scene.
That toggle tests the ordinary terrain path, not this cache or the RD prototype.

The static parity fixture's docks position is **1499,1761,-3**, while sustained
live timing uses **1497,1790,-5**. Do not compare their timing as the same scene.
The separate 360-frame static probe also had slower candidate means and higher
allocation; retain this contrary evidence. These windows can include cache
construction/churn:

| Static parity fixture | Before / candidate ms | Before / candidate KB allocated per frame |
|---|---:|---:|
| Bank | 8.789 / 9.399 | 19.3 / 31.8 |
| Forest | 5.127 / 7.191 | 18.7 / 20.5 |
| Dungeon | 4.503 / 4.854 | 19.3 / 28.5 |
| Docks | 8.056 / 9.653 | 19.3 / 27.1 |

The warm-hit microcheck allocates zero, but publication allocates retained arrays.
Conservative chunk invalidation can rebuild them. The whole-client observations
above are not an allocation improvement and need scrutiny before promotion.

## Profiling findings and limits

The earlier actual Release bank trace attributed sampled main-thread stack
residence to rect submission (35.4%), AddTileToRenderList (17.5%),
FillGameObjectList (10.3%), Resource.GetRid (6.6%) and DrawMeshSprite (6.5%).
After caching mesh RIDs, GetRid ceased being a sampled hotspot but native mesh
submission rose from 1.8% to 7.8%. The vanished wrapper attribution must not be
claimed as an equal amount of recovered frame time.

The later prototype trace highlighted AddTileToRenderList (15.2%), quad capture
(15.2%), native ptrcall (13.0%), FillGameObjectList (8.2%) and CoverFromBelow
(4.2%). Those observations motivated packet references, atlas metadata and
incremental candidate discovery. They are stack residence, not exact on-CPU
method durations, and use Debug Godot bindings.

The authorized 30-second native WPR capture of Release completed but lost
538 events and contained two invalid events. Salvaged module weights were
49.640% GUO.dll, 19.344% Godot-based executable, 10.958% CoreLib and 7.295%
NVIDIA driver, across the process. There were no matching native Godot PDBs.
This does **not** distinguish native submission, synchronization or driver cost.
Do not choose a C++ interface or engine patch from those percentages.

Fresh actual Release managed traces were collected for 30 seconds at bank and
docks, **after** their FPS sample intervals. Sanitized stacks and summaries are
in the current evidence run. Main-thread exclusive sampled stack weights:

| Stack / boundary | Bank | Docks |
|---|---:|---:|
| Native texture-rect submission | 35.1% | 34.3% |
| AddTileToRenderList | 17.4% | 18.0% |
| Native mesh submission | 13.5% | 11.8% |
| FillGameObjectList | 10.7% | 13.4% |
| CoverFromBelow | 3.5% | 3.7% |
| CoverCandidates.TryGet | 0.9% | Below the top twelve |

These are sampled stack residence, including native crossings/waits, not exact
on-CPU time. No current per-thread CPU endpoint deltas or matching native symbols
were collected. Do not sum them into a predicted FPS gain. Submission plus the
full scene walk still merits priority over further speculative cache expansion.

Useful native work still requires a private production build of the pinned
Godot revision with matching symbols, sufficient ETW buffers and per-thread
attribution. Replacing the engine or moving game logic to GDScript is not an
established optimization. A native bulk interface is a candidate only if the
reduced stream still measures a material crossing/submission bottleneck.

## What remains worth trying

1. Use the fresh bank/docks Release profile to rank the next preparation cost.
   Expand dirty dependencies only where the gain is measured. The full scene
   walk still controls roof breaks, lighting, picking and dynamic interleaving.
2. Test motion/crowds and cache churn. Idle wins do not establish walking or
   busy-bank wins; conservative chunk-wide invalidation may rebuild frequently.
3. Reduce unchanged ordered command/state construction in the RD prototype.
   Preserve per-occurrence order and frozen-ticket lifetime. Do not infer this
   work is gone merely because GPU geometry uploads are zero.
4. Evaluate camera-only updates separately with fractional zoom/translation and
   tile-seam byte/pixel controls before moving more subtraction to the shader.
5. Obtain useful native symbols, then decide on a bulk C++/GDExtension seam or
   engine change. Keep the ported game/network logic and authoritative ordering.
6. Baking/combining: keep immutable terrain/static geometry resident, invalidate
   affected chunks, and retain original atlas sampling. Replacing dynamic UO
   light, roof or transparency behavior with a permanent light bake would need
   explicit parity proof; it is not a free optimization.
7. The editor/non-rendering and device lanes are separate. Do not attribute
   their work or device acceptance to these PC tests. Allocation during travel,
   startup/assets, packets, pathfinding, gumps/text and audio still deserve
   focused profiles; the original broad audit is not complete.

## Evidence, review checklist and detailed reports

Current private run: `20261024_guo-codex-perf-implementation-20261022_native-symbols`.
It also holds the separate cover-membership and lazy-land evidence archives.
Earlier private run: `20261024_guo-codex-perf-implementation-20261022_static-candidates`.
Earlier private runs include `20261023_guo-codex-perf-implementation-20261022_release-cpu-hotpaths`
and `20261023_guo-codex-perf-implementation-20261022_atlas-cover-capture`.
Evidence belongs under the shared runs area, not in Git. Credentials, client
profiles, raw whole-system traces and proprietary artwork must not be published.

Fable should review invalidation coverage, capacity behavior, ordered oracle
tests, raw world differences and both faster/slower observations. Verify actual
build identities before comparing. Promote neither GPU mode from static-scene
success alone. Existing tests do not establish every live roof/CoT/picking,
fractional-camera or device combination.

- [Release covering handles](../2026-10-23_release_cover_handles.md)
- [Source records, atlas dimensions and profiling](../2026-10-23_persistent_sprite_records.md)
- [Atlas metadata](../2026-10-23_atlas_page_metadata.md)
- [First-Land identity cache](../2026-10-23_first_land_identity_cache.md)
- [Packet references and all contrary timing evidence](../2026-10-23_referenced_gpu_packets.md)
- [Ordered RD architecture](../../architecture/ADR-0043-ordered-rendering-device-lab.md)
- [Prototype limitations and counters](../../../godot/GUO/src/Labs/GpuRenderer/README.md)

Final current-run status: both frozen Release exports built successfully;
508 Release and 845 optimized Debug/Labs batcher checks passed, including the
50 new candidate-cache checks. The normal full repository smoke passed, as did
the four sampler-driver tests, docs lint, 114 launcher-pair checks and privacy
scan. Frozen world comparison and fresh bank/docks managed profiles completed.
Owned sampling clients were closed after their receipts were saved.

Review branch: `codex/incremental-cover-candidates`, based on `393c50e8`.
The change is a local review candidate, not a demonstrated general FPS win or an
approved default change on the integration branch. Whole-project review by Fable
is requested and remains separate from the source review already performed.

## Follow-up: retained ordinary-canvas commands

The next local experiment and all favorable/contrary measurements are in
[retained canvas commands](../2026-10-24_retained_canvas_commands.md).
It is off by default (`GUO_RETAIN_CANVAS=1`), independent of the GPU Labs mode.
First 30-second live Release observations under heavy, unequal shared load
were bank 78.384 off / 99.647 on FPS and docks 120.239 off / 138.473 on FPS.
Do not interpret those as isolated gains. Short static windows were mixed and
some retention-on allocation measurements were higher. Direct same-client
off/on captures matched every world pixel in all four scenes. Expanded tests
passed 573 Release and 912 optimized Debug/Labs checks; full smoke passed.
Review steady-state allocations and moving/live-world cases before promotion.

The follow-up 30-second live samples did not reproduce the short probe's
large allocation difference: bank approximately 20,805 bytes/draw off versus
20,409 on; docks 19,767 versus 19,746. Whole-process allocation includes
diagnostics and background work. FPS was bank 68.442/97.062 and docks
125.394/132.195, again with unequal shared load. The bank CPU maxima were
98.7% off versus 85.4% on. This is further observation, not an isolated gain
or proof of the earlier excess's cause. See the linked report for GC counts
and bytes/second as well as bytes/frame.

## Fable review received and next iteration

Fable completed the whole-client source/profile review and returned it via
fleet inbox messages 14031 and 14052. Its private review folder is
`guo_perf_review_2026-10-24` under the shared handoffs area; files include
`review.md`, `renderer_rd_cache_terrain.md`, `non_render_runtime.md` and
`editor_build_startup.md`. This source review is distinct from runtime
acceptance. The incremental candidate cache remains local and unpromoted;
its measurements did not establish a gain.

The bounded [gather-input candidate](../2026-10-24_gather_inputs.md) has
completed Release pixel checks and live invariant checks. Following Fable's
review, its measurements discarded 60 seconds after arriving rather than
the earlier 15. Bank measured 71.285 before / 74.058 after FPS, docks
121.416 / 123.827, with 96–100% host CPU maxima. These small differences do
not establish a gain; bank's measured preparation phase was slightly worse.
The candidate remains local. The report preserves all outcomes and limits.
The Release harness already rejects a Debug engine or unoptimized Godot API;
the shared count gate/export tool still needs an explicit Release workflow.

Next priorities are per-stage attribution and atlas upload diagnostics on a
longer moving run, then a separately flagged contiguous MultiMesh sprite-run
pilot if those results justify it. Atlas `Flush` currently updates an entire
dirty page, but the existing probe counts **LandPages array copies**, not all
ordinary atlas uploads. New counters must distinguish those costs and record
frame-time tails. Small pages or deferred uploads need measured tradeoffs;
deferring a newly referenced page or omitting undecoded sprites can alter the
visible frame, so those ideas are not automatically parity-safe.

The cache's invalidation should be narrowed only with ownership, absent-chunk,
detach/relink and movement coverage; do not trade correctness for a better
hit-rate counter. RD source/command optimizations after Fable's reviewed
snapshot remain separate and do not turn the prototype into a shipping path.
No public push, merge, default-toggle change or fresh CUO run is authorized
by this review exchange.

## Current next step after cover retention

Follow Fable's later ordering: measure retained canvas OFF/ON over a repeatable
real walk with per-second submission/visibility/upload counters, then attribute
steady allocations. The old duration-based walker can follow different routes;
do not label equal durations as an exact route replay. Retention still hides and
reveals retained items each frame, so count that native work. Its oversized-item
fallback retries recording each frame; test recovery after shrink and bounded
budget behavior before changing admission. Only then choose the next MultiMesh
pilot. Smaller art/animation atlas pages and a dedicated minimap texture remain
separate measured candidates; no upload deferral or visual changes are implied.

## Retained travel and allocation follow-up

The [travel attribution report](../2026-10-24_retained_travel_attribution.md)
records the next exact 46-tile bank/docks cohort, per-interval native work and
allocation traces. It uses 60-second warmups and 250 ms idle between completed
walk requests, so it is controlled interrupted travel, not continuous running.
First shared-load observations favor retention, but atlas upload counts and host
load differ. That cohort predates three diagnostic acceptance fixes and remains
provisional. Corrected reverse-order runs completed: bank 77.649 off / 67.624 on,
docks 93.376 off / 79.505 on FPS. Retention was slower, with worse tails and higher
per-frame allocation, in those runs. The conflicting outcomes establish no
general gain. All exact routes and pair conditions passed the hardened checks;
794 Release checks and four zero-difference pixel scenes passed. No default change follows.

Retention still makes roughly 3,130–3,270 native visibility calls per drawn frame
across those routes/cohorts. Allocation traces place 88.3%/89.1% of sampled bytes
in bank/docks UI drawing and 4.0%/3.8% in cover-run expiry; these are allocation weights, not CPU
percentages. The next bounded source targets are unnecessary debug closures and
expiry delegates/scratch storage, followed by measured UI command construction.
Keep those changes separate from the diagnostic work and the GPU experiments.

## Follow-up: incidental frame allocations

The [allocation patch report](../2026-10-24_incidental_frame_allocations.md)
records the unused debug-draw guard and reused cover-expiry scratch/predicate.
Actual Release bank/docks allocation fell from 19.6 KB per frame to 14.1/14.0 KB
in the 60-second-warmup, 30-second live samples. FPS was 61.778→79.899 at bank
and 84.826→96.174 at docks, with unequal shared load and atlas uploads; those
increases are not isolated speedup estimates. Separate allocation traces no
longer sample either targeted allocation site, with zero lost events/stacks.

798 Release checks passed. An unchanged-baseline control reproduced initial
cross-build terrain-edge differences at exactly the same pixels. Its repeat
matches the patch at bank, dungeon and docks; forest differs only in an inspected
858-pixel rain message. The raw variation is retained, not discarded as noise.
Final validation passes 798 Release / 1,137 optimized Debug/Labs checks, docs,
114 launcher pairs, privacy and whitespace checks. Full smoke reports aggregate
`OK` with the inherited `splat_lods_present` editor assertion still failing.

Next source preparation targets the roughly 3,000 redundant native visibility
calls in the retained-canvas experiment, with an explicit end-of-frame lifecycle
and abort recovery. It remains behind the existing off-default experiment;
ordinary Release and both GPU prototypes remain separate. Typed UI texture
commands have a source design, but no implementation or FPS claim yet.

## Follow-up: deferred retained visibility

The [deferred visibility report](../2026-10-24_deferred_retained_visibility.md)
now records completed standing and exact-route travel cohorts, final Release
identity, all call counts and contrary timings. Stable retained frames avoid
the old hide/reveal pair per owner; moving frames update only the changed
visible suffix. The main frame has explicit completion and abort recovery.
Transition tests also fixed native clipping being reset after a pooled owner
changed roles. Default retention is still off, and GPU ownership is excluded.

Final validation passes 940 Release checks and the preceding optimized
Debug/Labs build passes 1,280. Four full world captures match retention off/on
exactly. Independent source review found no new concrete defect, with no claim
of independent runtime acceptance. Full smoke has aggregate OK plus the
inherited editor `splat_lods_present` failure; docs, launchers and privacy pass.
UI texture command allocation work is the next separate local candidate.

## Follow-up: typed UI texture commands

The [UI command report](../2026-10-24_ui_texture_commands.md) records the next
local candidate. Three simple UI texture closures become ordered typed records;
mixed command size, deferred dimension reads, clipping, captions and existing
fallback semantics are preserved. Actual Release passes 996 checks and optimized
Debug/Labs passes 1,336. Login and all four full world scenes match the preceding
build pixel for pixel; the candidate also matches retained canvas off/on.

Bank allocation falls from 14.1 to 5.9 KB/frame; docks from 14.0 to 5.8. The three
targeted allocation sites disappear from separate loss-free trace samples.
FPS is worse in both shared-load observations: bank 76.627 to 67.855 and docks
103.247 to 92.428, with unequal atlas updates and no demonstrated CPU-time gain.
Fresh short-lived lists also allocate more at some populations, documented in
the report. This is a bounded allocation reduction, not an FPS success claim.

Next work prepares a pinned production Godot build with native symbols in an
isolated folder. It will support fresh CPU-versus-wait attribution; it does not
replace the configured engine or rehabilitate the earlier lossy trace. A
queue-order-preserving radix-sort candidate was prepared separately. No new
isolated FPS claim is made during our native build.

## Follow-up: exact world-depth sort

The [world sort report](../2026-10-24_world_depth_sort.md) records the integrated
local candidate, exact ordering argument, byte-for-byte integer oracles and
all scene observations. The measured Release sort stage falls from 0.447 to
0.091 ms at bank and 0.328 to 0.082 ms at docks. Whole-frame samples are bank
71.552 to 99.084 FPS and docks 100.422 to 101.568 under unequal shared load,
atlas updates and live workloads; these are not isolated FPS gain estimates.

Bank, forest and dungeon cross-build captures match exactly. Docks retains a
3,695-pixel raw difference confined to inspected server-save text; every pixel
outside that rectangle matches. The final Release build passes 1,182 checks,
including 186 sort checks; optimized Debug/Labs passes 1,522. The hot sort code
is unchanged from the measured candidate. Capture freshness was validated with
two new completed draws and the full integer oracle. Full smoke's first attempt
fails the editor pointer-picking fixture; the unchanged-source full repeat
passes. Both outcomes are preserved, with no editor-fix claim.

The pinned production native engine and matching PDB are built in an isolated
folder. DIA resolves the canvas renderer, canvas cull and rendering-server draw
functions to verified RVAs; a deliberately wrong PDB GUID is rejected. Fresh
engine validation and native collection remain separate from that symbol proof.
Do not reuse the old lossy trace as precise CPU attribution.

## Follow-up: cover membership validation

The [membership report](../2026-10-24_cover_membership.md) records a local
candidate that revalidates changed tile chains before discarding covering-land
plans. All mutation hooks remain active; retained identity and ownership
certification must both still match. Roofs, lighting, alpha, order and picking
remain live. Release passes 1,207 checks, optimized Debug/Labs 1,547 and full
smoke passes. Live stationary and exact-route reference oracles report zero
mismatches. An unchanged Release repeat reproduces the initial cross-launch
pixel variation and then matches the candidate exactly in all five scenes.

Walking plan rebuilds per draw fall about 76% at bank and 80% at docks.
Bank FPS is 69.147 to 70.602; docks 97.999 to 95.762, with slightly worse p95
and preparation timings. Atlas work and host load differ, and the report flags
our native compilation overlap. The repeated bank standing sample is 72.513
FPS under 49.8% mean host CPU load; docks standing is 117.449. These observations
do not establish a general FPS improvement or justify a renderer default change.

The elevated native collector was canceled by Windows before recording; no new
ETL was obtained. The [private native attribution report](../2026-10-24_native_render_attribution.md)
now records complete bank/docks and wall-only bank runs. Inclusive native
executed-draw elapsed means are 3.524/3.192/2.905 ms; canvas-tree means are
2.206/1.944/1.846 ms and end-frame means 1.104/1.051/0.896 ms. Scopes overlap;
do not add parent and child rows. Host load differs, and coarse thread-CPU
counters cannot establish exclusive CPU or wait time. No GPU-time claim follows.

Native source, production flags, matching PDB and bounded runtime coverage have
been checked. Disabled/enabled static captures match at four scenes after an
unchanged control reproduces terrain variation; bank still contains rain and
system-message differences, retained as an unresolved strict comparison. The
configured engine and ordinary Release exports are unchanged. Finer wall-only
canvas/end-frame scopes and a private optimized Debug-binding experiment are
the next investigations, with no default renderer or export-policy change.

## Restart continuation: matched API and native-v3 measurements

The [private optimized Debug API experiment](../2026-10-24_optimized_debug_api.md)
is now built and measured with actual loaded core MVID/optimization receipts.
Both matched sets retain DEBUG/TRACE, pass 1,557 checks, and match canvas/RD
pixels in all four static scenes. Eight sustained GUO-only live windows use
60-second warmup and 30-second measurement. Canvas normal/optimized API FPS is
80.046/92.522 bank and 106.833/119.544 docks; RD is 53.541/77.295 bank and
79.157/115.895 docks. Shared load varies substantially. Optimized RD is slower
than optimized canvas in both observations. This corrects a private prototype
measurement confounder; exported Release already has optimized bindings.

[Native-v3 attribution](../2026-10-24_native_render_attribution_v3.md) now has
231 isolated assertions, seven utility cases, matching EXE/PDB/DIA proof,
1,207 frozen Release checks, pixel controls and two complete wall-only windows.
Batch record/emit is 0.481/0.630 ms per draw at bank and 0.419/0.550 at docks.
Graph end is 0.796/0.727 ms, within end-frame 0.826/0.756. These are inclusive
elapsed scopes, not GPU or exclusive CPU time. The raw instance counter sums
Godot's high-bit push-data flag and is invalid as an instance count; that
preparation-note error is explicitly withdrawn. Raw values remain preserved.

Current checkpoint full smoke passes. No renderer/source/default/export guard
changed; no new CUO benchmark, computer use, push or merge occurred. Both GPU
experiments remain off by default. Earlier terrain variation and slower results
remain documented. Central private evidence is the `optimized-debug-api` run
under the resumed performance seat; neither overall performance acceptance nor
independent review is implied.
