# Exact world-depth radix sort

## Result and scope

The local candidate replaces the large world queue's comparison sort with
stable byte passes over the existing transformed depth key. Live Release
sort-stage observations fell from 0.447 to 0.091 ms at bank and 0.328 to
0.082 ms at docks. Whole-frame observations ran under unequal shared load;
they do not establish an isolated FPS gain. No CUO benchmark was repeated.

Layer: L0 parity-preserving implementation change at the GUO renderer seam,
marked `PORT DEVIATION (GUO)`. No player option, new rendering semantics,
engine dependency or default-toggle change. The ordinary Release path is
measured separately from both GPU experiments and retained canvas commands.

Base: local UI command commit `79a33c35`. Review branch:
`codex/world-sort-inputs`.

Rebased onto the perf integration (PR 130) for review, 2026-10-24: the sort,
its oracle tests and `render_sort_tests` are unchanged. Two parts that need
lane commits not in PR 130 are left out: the opt-in `GUO_PERF_SORT_INPUT`
capture diagnostic described below (its measurements were taken on the lane
branch), and the scratch release with terrain resources (there is no terrain
release seam in PR 130, so the scratch lives as long as its `RenderLists`).

## Why ordering stays exact

Immediately before sorting, `RenderLists.DrawWorld` builds keys in queue
order. Key `i` has unsigned low word `i`; its high word is the original
`DepthKey` float-bit transform. Stable sorting by all four high-word bytes
therefore produces exactly the same full unsigned 64-bit order as
`Array.Sort`. Equal depths retain original sequence, including repeated
translucent occurrences and covering land. No float comparison, depth
quantization, new tie breaker or spatial ordering is introduced.

`WorldDepthSort` is deliberately not a general unsigned-integer sorter.
Reordered or arbitrary sequence words violate its precondition. All current
world insertion sites were reviewed: each appends with `Seq = _world.Count`,
and no membership reorder occurs before the keys are built. Debug asserts
and live diagnostic captures check this invariant. Future callers must
preserve it or use the full sort.

Queues below 1,024 or above 65,536 entries use `Array.Sort`. An already
ordered queue returns without scratch allocation. Four byte histograms
share one input traversal; constant bytes skip their scatter pass. Every
active pass is stable, and odd pass counts copy back to the original array.

Scratch is lazy per `RenderLists`, capped at 512 KiB payload, retained by
ordinary `Clear` (on the lane branch, released with terrain resources). Captured bank/docks
queue sizes need about 130/103 KiB payload. A new owner reference adds
object storage; exact padding was not measured. Unused UI owners do not
allocate the key scratch array. Histograms use 4 KiB stack storage.

## Release measurements

Actual exported Release, 2560×1440, zoom 2, uncapped, VSync off, classic
lighting, PostFX off, retained canvas off and both GPU paths off. Each
location warms for 60 seconds before 30 seconds of measurement. Order was
bank before, bank after, docks after, docks before. The first bank-after
attempt disconnected during login; its failure is retained separately.
The retry is the bank-after observation below.

| Scene | Before FPS | After FPS | Before p50 / p95 / p99 ms | After p50 / p95 / p99 ms |
|---|---:|---:|---|---|
| Britain bank | 71.552 | 99.084 | 13.477 / 20.108 / 21.888 | 9.825 / 14.802 / 17.554 |
| Britain docks | 100.422 | 101.568 | 9.838 / 13.847 / 16.782 | 9.382 / 14.726 / 16.802 |

| Scene | Sort before / after ms | Prepare before / after ms | Draw before / after ms | Allocation before / after KB/frame |
|---|---|---|---|---|
| Bank | 0.447 / 0.091 | 5.746 / 4.129 | 4.425 / 3.011 | 5.6 / 5.6 |
| Docks | 0.328 / 0.082 | 3.704 / 3.930 | 3.097 / 2.768 | 5.8 / 5.5 |

Bank draws/seconds: 2,147/30.0062703 before; 2,973/30.0048891 after.
Docks: 3,013/30.0032448 before; 3,048/30.0093225 after.
Phase values are profiler observations, not a decomposition of every frame.

Host CPU mean/max over the whole run was bank 37.7/84.1% before and
30.5/84.6% after; docks 40.7/80.5% before and 48.8/95.4% after. Other
projects remained active. Our native-engine build and symbol-tool compilation
finished before these samples. Atlas updates were bank 9/8 and docks 14/0.
Bank covering-land count also differed (4,639/4,589); drawn statics matched
at 11,032. Docks static/cover counts matched at 9,151/3,268. These workload
and load differences prevent attributing the whole FPS change to sorting.

## Numeric replay measurements

An opt-in diagnostic captures numeric queue keys once per world scene,
outside the timed interval, and verifies the completed output against full
`Array.Sort`. No game objects, textures, account data or settings are stored.
The final diagnostic also requires two new completed client draws before
reading. Earlier measured captures predate this freshness fence; they passed
the queue/output consistency checks but do not claim the later fence.

A private .NET 8 replay rotates three algorithms for seven rounds using the
same reset copies and roughly two million elements per round. Results are
median microseconds per sort, including the identical reset copy:

| Captured scene | Entries | Array.Sort | Initial four-pass candidate | Adaptive candidate |
|---|---:|---:|---:|---:|
| Bank | 16,309 | 476.894 | 121.729 | 102.407 |
| Docks | 12,908 | 365.851 | 105.685 | 82.753 |
| Forest | 10,510 | 311.085 | 84.998 | 66.972 |
| Dungeon | 5,495 | 70.582 | 42.308 | 33.999 |

Every result matches the full integer oracle. The initial candidate lost on
small and nearly sorted synthetic queues; those findings motivated the
fallback threshold, ordered-input return and constant-byte skips. Replay
runs overlapped shared load and part of our native compilation. They isolate
the algorithm/input better than live FPS, but are not clean-room CPU results
or whole-frame gains. All rounds and contrary synthetic cases are retained.

## Correctness and pixel evidence

Production helper tests cover both thresholds, inactive tails, signed zeros,
extreme finite values, signed NaN payloads, equal-depth sequences, all fifteen
nonempty varying-byte combinations, warmed allocations and actual queue-like
inputs. The engine-free smoke tests call the production helper, rather than
maintaining a second implementation of the candidate.

Measured candidate v1 passes 1,160 Release checks. Final v2 expands the byte
skip coverage to 186 sort checks, for 1,182 Release checks. Production sort
code is unchanged between these builds; diagnostic freshness and test coverage
changed. Optimized Debug/Labs passes 1,522 checks. The final diagnostic
captured 15,608 bank keys after exactly two new completed draws and matched
the full integer oracle; its plain/configured captures also match exactly.

The first full smoke attempt exits 1 on the editor AI fixture
`tools_world_pointer_pick`: the reported hover cell and picked object disagree.
All sort and other smoke stages pass. This is retained as a real test failure.
The unchanged-source full repeat exits 0 with aggregate `OK`, including the
editor pointer fixture. The first failure's cause is not established; no editor
fix is claimed. Docs lint (324 documents), 114 launcher pairs, privacy and
whitespace checks pass separately.

Cross-build frozen captures compare three stable captures per scene. Login,
bank, forest and dungeon have zero differing pixels. Docks has **3,695 raw
differing pixels**, all within x=2..296, y=1325..1360: inspected captures show
the before client's yellow world-save messages, absent after. Every pixel
outside this rectangle is equal. Do not report all four whole images as
zero-difference. Same-client plain/configured captures have zero differences
in all four world scenes. This does not certify every live roof, transparency,
picking or device state.

## Build identities and private evidence

Control export `windows-world-sort-inputs-v2`, managed assembly SHA-256:
`cffca094b6a1e69650c875756d8d720c3f84263e293bed9e241a47978f1cb5de`.

Measured candidate `windows-world-sort-adaptive-v1`, managed assembly SHA-256:
`b4de6ea8d5d2d999858d7b86009309963de601d8c1ae33a843836b09db844b24`.

Final candidate `windows-world-sort-adaptive-v2`, managed assembly SHA-256:
`105cec88fdca4d688e90b5e8506c67bb0f28a1ef3603be651ba1ca5b6a14a24e`.

Evidence run:
`20261024_guo-codex-perf-implementation-20261022_world-sort`.
It contains source/check receipts, numeric replay inputs/results, full raw
pixel comparisons and shared-load samples. Credentials, client profiles,
proprietary captures and raw traces must not be published with the source.

The next preparation candidate narrows cover-membership invalidation only
after recertifying the complete linked chain. A Land-only shortcut is unsafe:
detaching a non-Land anchor can change which Land is reachable. Native CPU
attribution is a separate diagnostic lane; building a symbol engine is not
itself a renderer optimization.
