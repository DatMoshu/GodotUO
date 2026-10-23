# Persistent sprite records and atlas dimensions

This iteration removes two kinds of repeated CPU work: native dimension queries
for registered land pages, and packing/content comparison for unchanged chunk
quads in the ordered RenderingDevice experiment. The ordinary renderer receives
only the atlas cache. The GPU experiment remains off by default and restricted
to optimized Debug/Labs builds under ADR-0043; it is not a Release renderer.

## Source records and dirty updates

`MeshLayer` exposes an optional per-index revision seam. Ordinary non-Labs
compilation removes its partial calls. The Labs implementation allocates revision
storage only when a layer first enters the ordered capture. Geometry, hue, alpha
and pooled resets invalidate their affected records. The direct stretched-land
write explicitly notifies the same seam. Visibility and queue order do not change
content revisions.

Each source record keeps its original replay colors, exact packed quad and a
monotonically increasing immutable revision ID. Stable records skip quad creation,
four-vertex packing and 192-byte resident-content comparisons. Dirty sources
compare their final bytes before repacking: the existing reset-alpha/restore-alpha
sequence can otherwise invalidate identical content every frame. GPU residency
uses revision IDs for these records and retains exact content lookup for other
draws. Old and new revisions of a source can coexist in one frozen frame.

The cache is bounded at 65,536 source entries and sweeps unused owners. Capacity
overflow uses the existing exact-content path. Ordered occurrences remain distinct,
including repeated translucent cover draws. Texture owners remain retained;
early-prefix and late whole-frame canvas replay remain available. Static camera
subtraction retains the previous expressions and invalidates records when its
offset changes. Shaders and sampling are unchanged.

This is not yet an incremental world gather. The tile walk, roof decisions,
visibility, picking, covering-land discovery and sorting still execute. Dynamic
sprites still pack through the prior path, and frozen-frame vertices are still
copied for safe fallback. Those limitations are deliberate boundaries of this
change, not claims of eliminated work.

## Record validation

The optimized Debug/Labs build passed all 638 batcher checks (308 ordinary and
330 ordered-renderer checks), followed by the full smoke gate. Source tests cover
per-index hue/alpha/normal/geometry changes, unchanged zero-pack/zero-upload
repeats, transient alpha restoration, camera offsets, old/new revisions in one
frame, repeated translucent occurrences, pooled-layer reuse, content-cache
control, early-prefix fallback and late whole-frame replay. Residency checks
cover revision-ID reordering without content comparisons, mixed dynamic/source
records, eviction with later hits pinned, and invalidated upload retries.

Four live world runs used frozen paired DLL/vertex/fragment controls in
before/after/after/before order. Each run sampled 360 active RD frames per scene
at 2560x1440, zoom 2, with zero fallback. All 32 RD screenshots exactly matched
their same-run canvas references across all 3,686,400 pixels; 80 world screenshots
were retained. The docks candidate was also inspected visually. These are
GUO-canvas-versus-RD regression comparisons, not new whole-client CUO pixel
acceptance or coverage of every live movement/zoom/roof/picking combination.

| Scene | Reused source records/frame | Old RD callback ms | New RD callback ms |
|---|---:|---:|---:|
| Britain bank | 20,210 | 0.748–0.944 | 0.381–0.417 |
| Dense forest | 17,524 | 0.456–0.489 | 0.198–0.232 |
| Dungeon | 20,199 | 0.351–0.418 | 0.108–0.121 |
| Britain docks | 19,318 | 0.493–0.541 | 0.227–0.284 |

After warm-up the candidate packed zero new source records, performed zero dirty
source comparisons and uploaded zero geometry in these static scenes. Dynamic
draws still follow their original content path. The callback timer excludes CPU
capture, queued waiting and GPU execution. Whole-frame means overlap and sometimes
regress; no whole-frame speedup follows from this table. The preserved old RD DLL
is `239bc8e9`; the candidate also inherits the two later ordinary-renderer caches.
Separate same-candidate control runs isolate record reuse from those intervening
changes. All runs are under shared load. The first old world run also overlapped
private native-trace decoding; later world repeats and the CPU cohorts did not.
That run is retained and is not represented as an isolated timing measurement.

The separate CPU cohort contains 32 sequential observations: four scenes, two
rounds, GUO and CUO, before and after. Each second round reverses client order.
Viewport, zoom, cap, location, profile and frozen binary/shader identities were
checked. Host CPU reached 76.9% in the old cohort and 55.9% in the new cohort.

| Scene | Old RD-requested CPU ms | New RD-requested CPU ms | CUO CPU ms, four observations |
|---|---:|---:|---:|
| Britain bank | 11.184–14.327 | 11.537–12.991 | 4.090–5.552 |
| Dense forest | 8.353–9.006 | 8.895–9.640 | 3.805–4.825 |
| Dungeon | 6.082–6.258 | 5.011–5.579 | 2.698–2.950 |
| Britain docks | 7.229–8.478 | 8.517–9.119 | 3.632–3.952 |

These are elapsed CPU scopes, not FPS. The GPU builds optimize GUO's C# but still
use Debug Godot bindings and the editor engine; CUO is Release. This is therefore
a prototype diagnostic comparison, not an apples-to-apples shipping performance
ratio. Unlike the 360-frame world probes, the shared CPU dump does not record
per-frame active/fallback receipts; the table means RD requested, not proof that
every one of its 60 profiler entries used RD. The results do not justify claiming
a general performance improvement or enabling the prototype by default.

An additional pair uses the **same candidate DLL** with source reuse disabled
(`--rd-content-cache`) and enabled (the default within `--rd-world`). Both modes pass all four full-image comparisons
with 360 active RD frames per scene and zero fallback. This adds 40 private world
screenshots (16 RD comparisons), bringing live regression evidence to 120 world
screenshots and 48 exact RD comparisons. The disabled mode reports zero source
hits; the enabled mode reports the same 17,524–20,210 hits and zero newly packed
source records after warm-up.

| Scene | Content-control callback ms | Source-record callback ms | Control / records whole-frame mean ms |
|---|---:|---:|---:|
| Britain bank | 0.855 | 0.395 | 14.486 / 14.026 |
| Dense forest | 0.502 | 0.231 | 10.870 / 10.658 |
| Dungeon | 0.403 | 0.127 | 7.541 / 7.010 |
| Britain docks | 0.621 | 0.264 | 12.478 / 11.434 |

This control isolates the source-record switch from earlier commits. The callback
reduction is repeatable across the observed scenes. The modest whole-frame changes
are single pairs under shared load (host CPU maxima 58.1% and 59.1%); they are not
a reliable percentage gain or a 250 FPS result.

Sixteen additional sequential CPU observations compare the same candidate with
source reuse disabled against CUO, two rounds per scene. The enabled observations
are those in the preceding candidate cohort; these modes were not interleaved.
The control reports 13.804–14.694 ms at bank, 9.262–9.837 ms in forest,
6.532–6.658 ms in dungeon and 8.702–9.789 ms at docks. Its accompanying CUO ranges
are 4.083–5.239, 3.741–4.157, 2.702–3.224 and 3.467–4.008 ms, respectively.
Host CPU reached 59.1%. These additional observations support further work on
record reuse but retain the same Debug/API, shared-load, CPU-scope and backend
receipt limitations. All 48 prototype/CUO observations are retained.

## Atlas cache in ordinary Release

Commit `333e3e43` snapshots dimensions when a land page is registered. Registered
atlases are stable-sized; content updates retain their scale, while reset removes
the entries. Unregistered standalone textures still query their current size and
are not accidentally registered by a scale lookup. Dedicated oversized pages use
their actual dimensions.

The actual Windows Release export completed successfully and passed 308 batcher
checks, including five new dimension/reset/lifetime checks. Full smoke passed.
Fifteen sequential observations alternate the preserved `b35a00f9` control, this
candidate and optimized CUO at 2560x1440, zoom 2, cap 250, classic lighting and
post-processing off. Both GUO exports report the Release GodotSharp MVID
`011c9b9a-c180-4fe8-8104-e9758445ab22` and optimized engine/API code.

| Scene | GUO control CPU ms | GUO cache CPU ms | CUO CPU ms |
|---|---:|---:|---:|
| Britain bank, two observations | 7.019–7.448 | 6.754–7.141 | 3.889–4.028 |
| Dense forest | 4.137 | 3.953 | 4.127 |
| Dungeon | 3.854 | 2.242 | 2.787 |
| Britain docks | 4.284 | 5.092 | 3.319 |

CPU means `RENDER_FRAME + UPDATE_WORLD` elapsed profiler scopes, not whole-frame
time or measured FPS. The 60-entry ring is not proof of 60 newly settled frames.
Host CPU reached 74.4%; other projects remained unrestricted. Preparation time,
which the atlas patch does not change, varied substantially. These mixed results
verify a tested removal of calls, not a causal FPS improvement.

## Native profiling evidence and limits

The owner-authorized elevated WPR collector completed its bounded 30-second
CPU.verbose capture of the actual Release atlas-cache build at Britain bank.
Only the collector was elevated. Its named instance stopped, and the owned test
client was cleaned up. The initial unelevated attempt failed with profiling-policy
error `0xc5585011` and is retained as a failed attempt.

The native trace lost 538 events and reported two invalid events. Normal xperf
processing refused it; explicit `-tle` recovered diagnostic module aggregates.
Filtering to the owned GUO process gives 49.640% sampled weight in GUO.dll,
19.344% in the Godot-based GUO.exe, 10.958% in System.Private.CoreLib.dll and
7.295% in the NVIDIA driver. These are incomplete module weights across
the process, not exact function timings or a main-thread-only breakdown. Native
function symbols are unavailable. Snapshot CPU deltas also include start/stop
and trace-merge overhead and must not be divided by 30 as frame cost.

This evidence does not establish whether marshaling, engine submission,
synchronization or driver work explains the remaining native boundary hotspot.
Do not select an engine patch or C++ bulk API from these percentages. A repeat
with sufficient ETW buffers, matching native symbols and thread attribution is
still needed. Raw ETL and whole-system reports remain private; only filtered
aggregates are archived with this change.

The installed engine directory contains no native PDB. Godot's versioned
[4.7 profiling documentation](https://docs.godotengine.org/en/4.7/engine_details/development/profiling/index.html)
confirms that official binaries omit native symbols and recommends a production
build with symbols for useful native attribution. The next collector therefore
needs a private build of the pinned engine revision with `production=yes
debug_symbols=yes`, followed by a fresh trace of that exact binary. A different
build's symbols cannot repair this capture. This is profiling infrastructure,
not a proposed gameplay-language rewrite or an engine-version upgrade.

## Fresh candidate managed trace

A separate 30-second managed trace samples the frozen source-record candidate at
Britain bank after a 20-second settling interval. Its DLL and shader hashes match
the measured candidate; it uses the same optimized Debug/Labs and Debug Godot
bindings, with RD requested. The trace contains active `RenderRT` calls but does
not supply an every-frame backend receipt. The owned client exited afterwards.

The main thread has 30,016.8 ms of sampled stack coverage. Exclusive sample
weights identify `AddTileToRenderList` (15.2%), `OrderedWorldCapture.Quad` (15.2%),
the native ptrcall boundary (13.0%), `FillGameObjectList` (8.2%), and
`CoverFromBelow` (4.2%). The three gather/cover functions total 27.7% of this
thread's sampled stack weight. The main-thread endpoint CPU delta is 30.28
seconds, but per-method sample weights remain stack residence, not precise CPU
cost: they can include waits, native work and sampling attribution effects.

Attributing the native-boundary samples to their nearest Godot wrapper finds
2,394.4 ms under `Texture2D.GetWidth()` (8.0% of the thread's stack weight),
596.2 ms under `CanvasItemAddTextureRectRegion`, 355.9 ms under
`CanvasItemAddSetTransform`, and 125.5 ms under `Resource.GetRid()`. These are
subsets of the ptrcall category, not additional costs to sum with it. Width,
height and RID calls share the batcher's last-texture `Measure` cache; the trace
does not establish that the native width operation itself consumes this time.
Synchronization on the first native query remains a possible explanation.

Capture also retains sampled RID comparisons, transform/value helpers and
per-occurrence vertex/state copies despite source payload reuse. The Debug
Godot bindings affect this profile, so it cannot predict a Release implementation's
exact savings. It does provide concrete next targets: reduce capture to ordered
revision references, invalidate static gather dependencies explicitly, and test
atlas-owned metadata at the batcher boundary. Sanitized stacks and wrapper
attribution accompany the private evidence; raw traces remain private.

## Next architecture steps

1. Keep immutable source payloads resident through the entire submission path,
   not only on the GPU. Capture ordered IDs plus changing occurrence state, and
   materialize replay vertices only when fallback is needed. Payload lifetime
   must span the frozen render-thread ticket, including multiple revisions of
   one source in a frame. This targets the remaining per-frame vertex copies.
2. Introduce explicit invalidation for immutable chunk membership and covering
   candidates before caching the gather. Current `GameObject.RemoveFromTile`
   can unlink directly, and chunks can reload in place or return through a pool;
   a cache invalidated only by `Chunk.AddGameObject`/`RemoveGameObject` is unsafe.
   Cache static dependencies first, then reevaluate live alpha, roof, light,
   transparency, depth and picking decisions in their existing order. Measure
   candidate-cache hits and avoided tile visits before broadening the cache.
3. Treat world motion separately from idle scenes. Static records currently
   refresh on camera-offset changes to preserve exact rasterization arithmetic.
   Move that subtraction only after byte and pixel oracles cover all supported
   zooms, fractional camera transforms and terrain seams.
4. Reprofile the reduced stream with native symbols. A bulk native interface is
   justified only by measured residual crossing/submission cost; changing the
   language alone does not eliminate gather, copies or ordering work. Keep the
   CPU semantic order and picking authoritative while moving proven arithmetic
   and resident storage to the GPU.
5. Measure extending atlas-owned dimension/handle records to the batcher.
   `UltimaBatcher2D.Measure` remembers only the last texture, so A/B/A crosses
   the native width/height/RID boundary again for A. `TextureAtlas` owns the page
   creation/disposal seams, including oversized pages. Register immutable page
   metadata there; preserve fresh queries for unrelated resizable textures.
   Benchmark the added lookup cost against the avoided native calls separately
   from the land-page cache already implemented here. The candidate bank probe
   reports only 49 batcher texture switches despite 5,226 RD texture runs, so
   the RD run count must not be mistaken for native dimension-query count or
   used to predict a large saving here. The separate fresh managed trace does
   identify material stack residence at the width-query boundary. Measure this
   cache experimentally rather than inferring either a large or negligible
   saving from call counts alone.

No result in this report establishes a whole frame below 4 ms or attainment of
250 FPS. Preserving the existing pixels and reducing measured redundant work
remain separate gates from throughput acceptance.

## Evidence

Private run: `20261023_guo-codex-perf-implementation-20261022_atlas-cache-native-profile`
(story `PERF-RELEASE3`). The numeric archive contains the 63 CPU observations,
six world probes, exact-pixel receipts, build identities, frozen candidate patch,
filtered validation logs, reproduction helpers, sanitized managed stacks and
filtered native aggregates. A separate private archive holds the 120 world
screenshots. Raw profiles, credentials, ETL, nettrace and whole-system reports
are excluded. No UO art or private machine paths are added to the repository.
