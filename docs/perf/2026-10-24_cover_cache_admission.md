# Incremental cover retention — 2026-10-24

Locally validated candidate over `a64106c3`. L0 implementation
optimization: no new rendering mode, player option or visual semantics.
No public push, merge or isolated FPS gain is claimed by this candidate.

## Change

Cover plans retain ordered neighboring Land identities. Previously any chunk
load/unload changed a shared epoch and discarded every plan, including plans
whose consulted chunks were all still present and unchanged. Plans now record
whether they actually observed a missing chunk and carry their own slot epoch.
Ordinary slot churn invalidates those plans; other plans continue to validate
their exact chunk references and broad membership versions.

A separate table/lifetime generation still invalidates all plans on Map
construction or destruction. This is necessary because the World Map UI can
construct an auxiliary Map and replace the shared chunk array without destroying
the old Chunk objects. Checking only IsDestroyed or missing-slot dependencies
would retain a stale answer. Construction is conservatively a hard boundary
even when the existing array is large enough to be reused.

The existing membership invalidation remains broad. Removing or moving a
non-Land anchor can change the first Land or make a linked list foreign to its
cell. Filtering invalidation to Land objects is unsafe with these semantics.
Pooled reuse, reloads and all current link mutations retain their version checks.

When MaxEntries is full, a genuine miss no longer records candidate/dependency
lists that cannot be admitted. Existing hits are checked first, stale entries
can free their own slot, and the original live cover walk still renders the miss.
Rejected partial discoveries clear their references and count one rejection.
The independent reference limit and zero-reference plans retain their behavior.

Ground/height/width checks, alpha, depth, overlap, cover stamps, duplicate order,
roofs, lighting and picking remain live. The cache still stores identities only.

## Proof approach

Regression tests exercise mixed resident/missing plans in both query orders,
hard table replacement, discovery-time mutations, full-cache admission,
reference accounting, pooled ABA reuse, and non-Land/foreign-chain transitions.
The existing ordered QueueCover oracle is preserved.

`GUO_VERIFY_COVER_CANDIDATES=1` compares each cache hit with a fresh raw
GetHeadObject/TNext walk, without consulting the first-Land cache. It checks
Land identity, occurrence order, column and count and throws on a mismatch.
The verifier does not queue extra cover or change stamps. Its extra traversal
is for correctness runs only and must be disabled for performance samples.

## Frozen baseline

The baseline includes only interval cover counters on top of `a64106c3`.
DLL SHA-256:
`9b5809a702892be787cd23989c808d8ae50f8a65593e9a3fac8d0a7bbd6b656c`.
Actual Windows Release engine and optimized GodotSharp were checked at runtime.
No CUO client ran. Retained canvas and both GPU experiments were off.

The probe uses Forward+, a 2560×1440 window, zoom 2, plain profile, PostFX off,
live world, a 60-second discard, then 30 seconds standing or 60 seconds walking.
It uncaps both engine and client pacing. Bank is 1434,1699,0; the pier docks
fixture is 1499,1761,-3 on map 0. The observed walk starts at bank and turns
when blocked; it is not an exact replay. All results are under shared-host load.

| Baseline scene | Draw FPS | Prepare ms | World draw ms | Cover hits | Misses/builds | KB/frame |
|---|---:|---:|---:|---:|---:|---:|
| Bank | 80.721 | 4.851 | 3.924 | 2,705,374 | 0 | 19.5 |
| Pier docks | 98.466 | 3.678 | 3.084 | 2,033,015 | 25 | 19.6 |
| Bank-origin walk | 63.869 | 5.783 | 5.255 | 3,470,699 | 75,090 | 60.9 |
| Pier-origin walk | 75.669 | 4.976 | 4.131 | 2,964,220 | 35,403 | 34.8 |

The baseline walk observed 185 tiles. Host CPU reached 95.2% across launch,
warm-up and measurement. No capacity rejections occurred in these scenes;
therefore admission skipping is a bounded stress-case improvement, not an
explanation for any steady-state FPS movement here. Bank already had zero
rebuilds. The measured opportunity is travel-time rediscovery/allocation.
The separate pier walk observed 180 tiles in 60.011 seconds, with host CPU
reaching 100%. Its p95/p99/max frame times were 20.994/23.950/123.893 ms.

## Candidate results

Optimized Debug and actual Release v1 passed all 697 batcher checks, including
153 candidate-cache/verifier checks. A source-only independent review found
no concrete invalidation/order defect; it did not claim runtime acceptance.

Release v2 added a probe-only cache-off/on comparison. Ordinary play
still retains candidates. `GUO_PERF_PARITY_COVER_CANDIDATES=1` selects the
live uncached suffix versus candidate retention, with prior state restored
in `finally`. This isolates cache behavior without changing sessions or facing.
Its DLL SHA-256:
`cd5d50de270fe8c47f087872fc66a41e5e944e4367577f128f3c54a9ae33f880`.

The initial direct five-frame comparisons found zero changed stable pixels at bank,
forest, dungeon and docks, with all 3,686,400 world pixels stable in each.
These are same-client cached-versus-live checks, not a new CUO comparison.
The accompanying short timings favored the uncached path in all four scenes
(bank 78.106 cached / 82.619 uncached FPS; docks 91.291 / 103.028). World draw,
which this cache does not optimize, was also lower in each second interval.
Shared load and short sequential sampling prevent a causal conclusion; retain
this contrary evidence and do not claim a cache FPS gain from the pixel test.

The earlier cross-build comparison against `runtime-parity-off` did **not**
match: stable differences were bank 12,476, forest 459, dungeon 16,725 and
docks 1,873 pixels. Saved crops visibly show changed player facing and a
shifted light pool; not every difference was attributed. The raw failure is
preserved. It is not silently masked or relabeled as exact cross-build parity.
The controlled same-client cache-off/on comparison is the intended pixel proof
for this change, with the stronger draw fence below required for acceptance.

The first live-verifier attempt disconnected before the character list and
produced only a login row; it is not accepted as a world verification run.
The successful replacement runs checked 134,133,925 raw Land references with
zero mismatches across bank, dungeon, docks and walking, with both binary and
gradient circle-of-transparency profiles. Walks observed 78 and 68 tiles in
20-second intervals. These diagnostic FPS values are not performance results.

Review found an inherited gap in the pixel harness: two engine post-draw
signals could occur while client pacing suppressed DrawFrame. V3 now waits
for two actual client/batcher frames after each toggle, checks the selected
state, and records actual draws, engine signals and cache hits for each image.
A ten-second fence rejects continuing signals without sufficient client draws.
Synthetic tests cover suppressed draws, first/second draws, timeout, state
drift and counter reset. The external driver also rejects an unexercised world
target. This is test infrastructure; ordinary drawing and pacing are unchanged.

Sustained-timing Release v3 DLL SHA-256:
`79cc5852253734c514bb93867220841a680eaff720be7506c2f6a1a7c579d1e3`.
It passed all 699 batcher checks. All four scenes were rerun: every capture
recorded two actual draws; each off capture recorded zero candidate hits.
Each on capture recorded 2,234 hits at bank, 628 in forest, 1,266 in dungeon
and 1,376 at docks. All 3,686,400 pixels in each world scene were stable and
matched exactly, with zero single or repeated differences. This supersedes
the initial captures as direct cache-versus-live pixel evidence. It does not
retroactively certify older captures without actual-draw evidence.

Final draw-fence follow-up also checks the deadline before accepting a late
second draw, independently bounds the awaited signal with remaining-time
`WaitAsync`, and makes unexercised world cache captures fail the probe result.
Tests cover a never-completing signal and a completed signal after expiry.
This V4 diagnostic-only delta is outside ordinary drawing and timing paths.
Final V4 Release DLL SHA-256:
`c96800036bce3245c8b8c96822d2c5a3138f84c69d1d7a831aa5e8ee1c193340`.
All 700 Release and 1,039 optimized Debug/Labs batcher checks passed. The four
world scenes again had zero differing pixels, with actual-draw and target-hit
assertions passing. Full smoke returned aggregate OK; its editor subprobe still
reports the inherited `splat_lods_present` failure. This is not an all-green
editor result. The sustained numbers below are explicitly V3; V4 changes only
diagnostic timeout and acceptance handling, not the measured render path.

## Sustained Release observations

The candidate samples use the exact V3 Release export identified above, with
verifier and parity switches off. Same 60-second warmup and 30/60-second
measurement configuration as the frozen baseline. No own build ran during
these intervals. Other projects remained active; host CPU maxima across
launch/warmup/measurement were 95.2% before / 99.2% after for bank+docks+walk,
and 100% / 96.2% for the separate pier walks. This is not a quiet-host result.

| Scene | FPS before → after | Mean ms before → after | Prepare ms before → after | KB/frame before → after | Cover builds before → after |
|---|---:|---:|---:|---:|---:|
| Bank | 80.721 → 85.601 | 12.388 → 11.682 | 4.851 → 4.588 | 19.5 → 19.5 | 0 → 275 |
| Pier docks | 98.466 → 93.213 | 10.156 → 10.728 | 3.678 → 4.162 | 19.6 → 19.5 | 25 → 158 |
| Bank walk | 63.869 → 70.995 | 15.657 → 14.085 | 5.783 → 5.224 | 60.9 → 49.1 | 75,090 → 22,988 |
| Pier walk | 75.669 → 79.559 | 13.215 → 12.569 | 4.976 → 4.846 | 34.8 → 30.5 | 35,403 → 4,950 |

The bank walk's 186 baseline map/XYZ observations are an exact prefix of the
candidate's 188: 185 tiles before, 187 after. The pier walks have identical
181-point observed map/XYZ routes, 180 tiles each. Both after runs lasted at
least 60 seconds and reported zero dropped route observations or sample-storage
growth. Matching observed positions does not establish identical timing,
acknowledgements, dynamic NPCs, asset history or host contention.

Travel rediscovery fell substantially despite more drawn frames: bank
3,833 → 4,260 frames and pier 4,541 → 4,775. Allocation also fell per frame;
bank Gen0/1/2 counts were 6/3/1 → 4/1/0, pier 3/1/0 → 3/1/0. No capacity
rejections occurred, so these runs exercise scoped invalidation, not the
full-cache admission shortcut. Stationary results are mixed and bank already
had no baseline builds. This supports reduced travel retention work, not a
general or isolated FPS gain.

| Scene | p95 before → after ms | p99 before → after ms | Maximum before → after ms |
|---|---:|---:|---:|
| Bank | 18.106 → 16.350 | 20.391 → 18.380 | 23.371 → 21.820 |
| Pier docks | 15.314 → 15.821 | 17.240 → 17.712 | 20.950 → 20.173 |
| Bank walk | 24.302 → 20.623 | 28.282 → 24.318 | 53.224 → 52.843 |
| Pier walk | 20.994 → 18.671 | 23.950 → 20.915 | 123.893 → 31.680 |

The removed worst pier spike is not attributed to this cache change. Whole
frames remain well above the 4 ms / 250 FPS target at this size and zoom.
The next priority is retained-canvas OFF/ON travel, per-second submission and
visibility counters, and steady-state allocation attribution before a new
MultiMesh prototype. The existing retained-canvas and GPU experiments remain
off by default and were off in every sustained row above.

Private evidence run:
`20261024_guo-codex-perf-implementation-20261022_cover-cache-admission`.
