# GUO performance review for Fable — 24 October 2026

Owner: Moshu. Implementation/review: Codex, continuing Fable's 22 October
handoff. This consolidates the rendering/performance lane through the local
incremental-cover work. It distinguishes shipped-path Release work from the
off-by-default GPU experiment. It is a review record, not approval to merge or
enable either GPU mode.

## Current assessment

GUO now avoids substantial repeated work, with explicit lifetime and pixel
checks. Bank and docks still have expensive CPU preparation and submission.
There is no established general 250 FPS result at 2560×1440, zoom 2. That target
requires the entire frame to fit within 4 ms. Callback timings, profiler CPU
scopes and reduced draw counts cannot independently establish it.

The owner has changed the workflow: **stop rerunning CUO cohorts**. Make bounded
GUO changes, load the fixed locations and record sustained GUO FPS and phase
timings. Keep all attempts and label shared host load. The latest actual Release
comparison records 30 seconds per location. The current candidate removes work,
but has not established a throughput gain: the first bank sample is slower and
docks is approximately unchanged. Keep it as a local review candidate.

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
| Current local delta | Incremental covering-land candidate discovery | Repeating neighboring tile/chunk discovery for static objects | Live alpha/depth/overlap/stamps, roofs, lights and picking remain authoritative |

PR references identify the existing stack, not a fresh remote-state audit.
Only the previously authorized pushes through PRs 152/153 were made. The three
following commits and current delta require review; no new push or merge is
implied by this document.

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

## Current incremental preparation change

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

Actual Release currently passes **508 batcher checks**, including 50 new cache
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

Current private run: `20261024_guo-codex-perf-implementation-20261022_static-candidates`.
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
