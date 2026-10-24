# Ordered GPU capture through persistent packet references

Layer: L4, ADR-0043. The existing `GuoLabs=true` Debug-only `--rd-world` toggle
remains off by default. This change is excluded from exports. It follows the
atlas metadata and first-Land identity changes, which have their own actual
Release/CUO measurements; no GPU result belongs in those Release tables.

Unchanged cached statics and terrain now append an ordered packet reference
instead of copying four 48-byte vertices and their content ID each frame.
Uncached dynamic draws retain their existing packet construction and exact-byte
comparison. Visibility, transforms, texture batching, ordering, lighting, shader
arithmetic and nearest sampling are unchanged. GPU slot contents are copied only
when residency requires a dirty/new slot.

Packets remain immutable while captured. Multiple revisions of one source within
a frame coexist; the next successful Begin retires the previous ticket before
reusing current, superseded or expired packets. A pending rendering callback
blocks retirement. Reused packet objects receive fresh monotonically increasing
IDs, so earlier resident geometry cannot alias a new revision.

Canvas rollback reads the same ordered references directly, with original
command colors and rectangle arithmetic. It constructs native canvas work only
on rejection and retains no second expanded vertex buffer. Record expiry and
the spare pool are bounded; the spare cap is not a total live packet cap. Source
records, dynamic capture, state rows, ordered commands and failed-frame native
canvas meshes still cost memory and CPU time. This does not remove full world
traversal or move static camera subtraction into the GPU.

## Validation

The optimized Labs build passes all 795 batcher checks, including 337 ordered-RD
checks. New controls prove reference-only unchanged capture, camera updates with
no new packet allocation, 64 tickets with two same-frame revisions using two
packets, 16 expiration/replacement batches without new packet allocation, and
fresh content IDs after recycling. Active, early-rejection and late-failure pixel
tests now exercise three coexisting revisions of the same source. Existing
shader, alpha, hue, stretched land, clipping, resize, order, upload retry and
cancellation checks remain passing. Normal non-Labs full smoke also passes.

An independent static review found two allocation/retention issues in the first
proposal: expired packets missed the spare pool, and replay expanded a second
full vertex buffer. Both were corrected before this build. A second review found
no blocker in those corrections. Review is source evidence, not a performance
claim or a replacement for the actual pixel tests.

## Measurements

Frozen world labels `rd14-references-shared-measured-world-before-1` and
`world-after-1` compare the first-Land cache baseline with this packet-reference
candidate. Each scene samples 360 active RD frames, with zero fallback. All 16
same-run RD/canvas comparisons are byte-exact across 3,686,400 pixels. Direct
cross-build canvas comparison is also exact at bank, forest, dungeon and docks.
Both saved RD captures per scene used the pass. DLL and unchanged shader inputs
were hashed before/after; owned-client cleanup is verified.

| Scene | Referenced quads/frame | Dynamic vertex bytes/frame | New packets/frame | Replayed quads/frame |
|---|---:|---:|---:|---:|
| Britain bank | 20,210 | 2,138,880 | 0 | 0 |
| Dense forest | 17,524 | 1,745,856 | 0 | 0 |
| Dungeon | 20,199 | 230,016 | 0 | 0 |
| Britain docks | 19,318 | 1,775,040 | 0 | 0 |

Bank avoids 3,880,320 bytes of repeated cached-source vertex copies per frame;
docks avoids 3,709,056 bytes, computed as referenced quads times 192. These are
removed logical vertex copies, not measured DRAM traffic, total-allocation
savings or a claim of zero per-frame work. Dynamic copies and reference/state
streams remain. Warm GPU geometry uploads are zero in both builds.

The separate PerfProbe elapsed-frame observations are:

| Scene | Before mean ms | After mean ms | Before p95 ms | After p95 ms |
|---|---:|---:|---:|---:|
| Britain bank | 13.978 | 14.466 | 17.572 | 18.550 |
| Dense forest | 10.950 | 10.774 | 14.483 | 13.256 |
| Dungeon | 7.804 | 6.135 | 11.490 | 8.063 |
| Britain docks | 12.833 | 11.974 | 16.004 | 15.861 |

These optimized GUO Debug/Labs builds use Debug Godot bindings. This table is
neither Release performance nor a CUO FPS comparison. Each cell is one 360-frame
window under **shared load**. Host CPU peaked at 50.5% before and 53.9% after;
prestage GPU peaks were 28% and 35%. Mixed results, including the slower bank,
do not establish a reliable speedup. Allocation remains about 20 KB/frame.
All unrelated projects were unrestricted. Earlier sandbox output failures are
retained separately and occurred before a game client was launched.

The interleaved cohort (`rd14-references-paired-01-control` through `12-cuo`)
contains two observations per client per scene, reversing order. Coordinates,
2560x1440 camera bounds, zoom 2, cap 250, classic lighting and post-processing off
are checked. The GUO entries below are still optimized Debug/Labs with Debug
Godot bindings; CUO is actual Release. Do not treat this as a fair shipping-build
FPS ratio. The comparable GUO change is control versus candidate within this
same prototype configuration.

| Scene | Prior prototype frame ms | Reference prototype frame ms | CUO Release frame ms |
|---|---:|---:|---:|
| Britain bank | 13.941–13.991 | 13.095–13.180 | 4.383–4.949 |
| Britain docks | 8.250–8.442 | 7.621–9.089 | 3.983–4.148 |

| Scene | Prior prototype CPU ms | Reference prototype CPU ms | CUO CPU ms |
|---|---:|---:|---:|
| Britain bank | 12.818–12.983 | 11.933–12.046 | 4.219–4.770 |
| Britain docks | 7.561–7.623 | 6.995–8.073 | 3.484–3.696 |

CPU means `RENDER_FRAME + UPDATE_WORLD`, not total frame time or GPU execution;
the RD callback is outside the ordinary world-draw scope. Frame time is the
profiler's 60-entry wall-frame ring, distinct from the preceding 360-frame
PerfProbe table. The average bank wall interval is about 6% lower in this
interleaved cohort; docks is essentially unchanged, with one slower and one
faster candidate observation. This is promising bank evidence, not a proven
isolated gain, and the contrary bank PerfProbe observation remains in the report.

All runs are **under shared load**, with host CPU maxima of 40.6–68.2%. All four
candidate timing windows verify active RD (147/164 bank frames and 203/246 docks
frames), zero fallback and zero new packet allocations. Control binaries predate
these PerfDump backend fields: their toggle was requested, their world probes
verify active RD, and their timing logs contain no fallback message, but they
do not supply the new per-window active-frame receipt. This limitation is not
filled with an inferred counter. Candidate restoration and cleanup are verified.

## Remaining work

The ordinary Release results for atlas metadata and first-Land caching remain
mixed under load; this experiment does not change those exports. None of these
data establishes the whole-frame 4 ms target at this viewport. Full incremental
static/candidate gathering, motion-specific capture, live gameplay acceptance,
fractional-camera world parity and non-PC RD validation remain open. A fresh
profile of the reduced stream, with matching native symbols, should determine
whether the next measured cost is source lookup/capture, submission or engine
work before choosing a C++ interface or engine change.
