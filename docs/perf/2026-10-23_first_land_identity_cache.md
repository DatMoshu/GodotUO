# Incremental first-Land identity lookup

Layer: L0 backend optimization with marked port deviations. The owner authorized
incremental preparation, bank/docks measurements and unchanged rendering. This
step caches the original linked traversal's first `Land` identity per chunk cell.
It does not cache cover winners, height, alpha, lighting, visibility, picking or
sort order. Those decisions still evaluate live objects every frame. Static
gathering and covering-candidate discovery remain future work.

A 64-entry reference array and valid-bit mask distinguish a cached null from an
unvisited cell. Objects remember their actual tile membership independently of
XYZ, which upstream changes before unlinking. Insertion, unlink, reload, destroy
and chunk reuse invalidate affected cells. Cold scans certify the whole chain's
ownership; mixed or detached legacy anchors remain uncached. No linked-list or
anchor behavior is repaired as part of this optimization.

Warm hits read the bit and reference directly. The first iteration checked the
anchor and membership on every hit; review established that the valid bit already
certifies ownership until invalidation. The final implementation removes that
redundant hot-path work. Cold misses scan the complete chain, so they can cost
more than the original early-exit search. Added retained memory includes 64
references per chunk and membership fields per game object.

## Validation

The final optimized Labs build passes 788 batcher checks and the actual Windows
Release export passes 458. The 138 new checks compare cached results with the
original head/TNext traversal, including cached null, equal-depth ordering,
direct unlink, XYZ-before-move, detached/foreign anchors, mixed ownership,
bulk cleanup through aliases, preserved players, invalid-index load and pooled
reuse. Changes to live alpha and land geometry remain visible through the cached
identity. Full smoke passes. The cache was independently reviewed, including
the invariant permitting a direct hot hit.

Frozen before/after world runs each sample 360 active RD frames at bank, forest,
dungeon and docks with zero fallback. All 16 same-run RD/canvas comparisons are
byte-exact across all 3,686,400 pixels. Direct cross-build canvas comparison is
exact at forest, dungeon and docks. Bank differs at 12,550 pixels, confined to
bottom-left system messages (town guards, chat, rain and server world-save text);
the actual before/after crop was inspected. World art is unchanged in these
captures. Raw HUD differences are retained. This does not establish whole-client
CUO pixel acceptance or all dynamic gameplay combinations.

Private world labels are `rd13b-first-land-shared-measured-world-before-1` and
`world-after-1`; frozen DLL/shader closure and owned-client cleanup are verified.
Host CPU peaks are 46.4% and 57.7%; prestage GPU peaks are 29% and 33%. These
optimized Labs/Debug-API runs supply pixel evidence, not Release throughput.
An earlier sandbox attempt failed on output permissions before client launch;
its separate failed receipt remains available.

## Actual Release comparisons under shared load

Each round has twelve sequential observations: two per client per scene, with
reversed ordering of atlas-metadata GUO control, candidate and CUO. All use
2560x1440, zoom 2, cap 250, classic lighting and post-processing off. Both GUO
executables use the Release engine and optimized Release Godot bindings. The
GPU prototype is absent from these exports and tables.

Final direct-hit candidate (`release6-first-land-hot-paired`):

| Scene | Control GUO frame ms | Candidate GUO frame ms | CUO frame ms |
|---|---:|---:|---:|
| Britain bank | 8.539–11.406 | 9.794–19.503 | 4.070–5.307 |
| Britain docks | 5.545–5.648 | 5.099–6.211 | 4.074–4.363 |

| Scene | Control GUO CPU ms | Candidate GUO CPU ms | CUO CPU ms |
|---|---:|---:|---:|
| Britain bank | 6.264–8.634 | 7.396–15.214 | 3.825–5.126 |
| Britain docks | 4.044–4.149 | 3.656–4.604 | 3.378–4.100 |

CPU is `RENDER_FRAME + UPDATE_WORLD`; it excludes frame pacing and is not FPS.
Frame time uses the existing 60-entry wall-frame ring. **Every observation is
under shared load.** Host CPU maxima range from 44.4% to 88.1%; the 19.503 ms bank
candidate is the run with the 88.1% maximum. That maximum covers the run, not a
proven causal attribution of each measured frame. Both scenes contain mixed
before/after results. These data do not establish a reliable FPS improvement;
the slower observations are retained, not discarded as outliers.

Candidate counters show 48,964 bank and 21,536 docks lookups per process frame.
Scans round to 0.0/frame; forward node visits are 0.0–0.1/frame. These are window
averages, not a claim that no mutation or miss ever occurred. The old control
lacks these counters. The deterministic improvement is eliminating repeated
unchanged chain walks, not yet a measured whole-frame speedup.

The earlier checked-hit iteration is retained as `release5-first-land-paired`:

| Scene | Control GUO frame ms | Earlier candidate frame ms | CUO frame ms |
|---|---:|---:|---:|
| Britain bank | 8.174–10.204 | 8.979–10.730 | 4.149–5.515 |
| Britain docks | 6.142–6.264 | 5.074–5.885 | 4.002–4.365 |

Its CPU ranges were bank 6.042–7.675 / 6.728–8.216 / 3.927–5.323 ms and docks
4.501–4.569 / 3.638–4.360 / 3.233–4.082 ms (control/candidate/CUO).
Host CPU peaked at 82.9%. It is a separate build and cohort, not pooled with the
final version to improve a headline result. Source patches and executable
identities accompany both rounds in private evidence outside the checkout.
