# Retained canvas travel and allocation attribution

This is diagnostic work on the ordinary Release renderer. It changes no default
rendering mode. `GUO_RETAIN_CANVAS=1` remains an off-default experiment, separate
from both GPU prototypes. No CUO comparison was run.

## Measurement contract

Each live-world leg discards 60 seconds at its origin, then follows the same
private 47-point map/X/Y/Z artifact: 46 adjacent tile transitions. The controller
uses real player walk requests and server acknowledgments, one request in flight,
with a **250 ms idle interval after each completed request**, including turns.
These are controlled interrupted-travel samples, not continuous full-speed runs.
Bank starts at `[go 1434 1699 0`; docks starts at `[go 1499 1761 -3`.

Both pairs have identical route hashes, initial facing, mount/flying/speed/run
conditions, and completed coordinates. Bank has 50 requests/ACKs (four turns);
docks has 55 (nine turns). Each leg records two subsequent client draws. The
external driver independently bounds process lifetime and cleans up only its
own processes. Profiles and route artifacts remain private.

The new opt-in interval sampler reports actual elapsed boundaries, frame-time
quantiles, retained comparisons/submissions, canvas clears/hides/reveals, atlas
updates/bytes, land uploads and GC allocation/count deltas. It preallocates its
storage before timing and constructs report objects afterward. Delayed intervals
are not fabricated into multiple one-second samples. Zero-draw ratios are null;
raw work is retained. Process allocation is an approximate runtime counter and
can differ slightly from the exact current-thread counter.

## First frozen cohort: observations, not acceptance

Actual exported Release, optimized Release Godot API, 2560×1440, zoom 2,
classic lighting, PostFX off, live world, cap/vsync disabled during measurement.
DLL SHA-256: `de50f1481ab523565eb8ed2834993a0c355116f1874d02e6d18afc4c4edab61a`.
Order: bank off/on, docks on/off. Other projects remained active.

| Route | Retention | Draws / seconds | FPS | Mean / p95 / p99 ms | Prepare / draw ms | KB/frame | Peak host CPU |
|---|---|---|---:|---|---|---:|---:|
| Bank | Off | 1,276 / 22.424 | 56.903 | 17.574 / 24.635 / 27.485 | 6.914 / 5.822 | 40.6 | 98.3% |
| Bank | On | 1,602 / 22.571 | 70.977 | 14.089 / 19.887 / 22.509 | 5.495 / 4.608 | 36.0 | 99.8% |
| Docks | Off | 2,037 / 23.555 | 86.477 | 11.564 / 17.604 / 20.254 | 4.197 / 3.533 | 27.4 | 99.6% |
| Docks | On | 2,124 / 23.540 | 90.228 | 11.083 / 16.773 / 20.525 | 4.015 / 3.577 | 26.7 | 76.9% |

FPS favors retention in this cohort, but unequal host load and different asset
arrival/upload counts prevent causal attribution. Docks world-draw time and p99
were slightly worse with retention. Do not promote the toggle from these rows.

Source review also found acceptance gaps in this frozen diagnostic binary:
terminal state and observer ownership were not rechecked after the final draw
drain; cleanup failure could occur after the success dictionary was copied; a
late second draw could escape the drain deadline; setup turn completion lacked
an ACK/denial witness. There is no evidence those races occurred in this cohort,
but these rows remain provisional. The corrected rerun follows below.

## Corrected cohort

The new binary validates terminal state and restores its observer before copying
success, requires a setup-turn ACK, and bounds/checks the final draw deadline
before accepting it. DLL SHA-256:
`0f030713374db96a8bae1bcbe1f29fc393fe90bde3f7837c86f31ec6313aeaf7`.
Order is reversed: bank on/off, docks off/on. All four legs passed the hardened
route acceptance, exact completed-point and paired-condition checks, with the
same warmup/configuration and no dropped interval storage.

| Route | Retention | Draws / seconds | FPS | Mean / p95 / p99 ms | Prepare / draw ms | KB/frame | Peak host CPU |
|---|---|---|---:|---|---|---:|---:|
| Bank | Off | 1,709 / 22.009 | 77.649 | 12.879 / 18.812 / 21.466 | 5.063 / 4.098 | 34.1 | 99.8% |
| Bank | On | 1,516 / 22.418 | 67.624 | 14.788 / 21.071 / 24.212 | 5.561 / 4.878 | 37.6 | 98.1% |
| Docks | Off | 2,181 / 23.357 | 93.376 | 10.709 / 16.324 / 19.540 | 3.965 / 3.411 | 26.5 | 100.0% |
| Docks | On | 1,882 / 23.672 | 79.505 | 12.578 / 19.261 / 22.800 | 4.790 / 3.903 | 28.5 | 88.8% |

Both observed ratios reversed: retention is slower in this cohort, including
world-draw time and tails. Load/upload counts remain unequal. There is no reliable
overall gain or allocation improvement established; keep the toggle off.

## What the native counters establish

| Per client draw | Bank off | Bank on | Docks off | Docks on |
|---|---:|---:|---:|---:|
| Owner canvas clears | 1,852.0 | 285.3 | 1,852.0 | 152.1 |
| Retention rewrite clears | 0 | 362.3 | 0 | 338.6 |
| Hide + reveal calls | 0 | 3,127.6 | 0 | 3,166.4 |
| Records compared | 0 | 10,248.9 | 0 | 9,615.1 |
| Prefix records matched | 0 | 4,698.9 | 0 | 5,467.8 |
| Retention-path records submitted | 0 | 10,594.4 | 0 | 7,069.9 |

These are the corrected cohort's counts. The first cohort had 3,273/3,253
hide/reveal calls per bank/docks draw; both show the same remaining lifecycle cost.

The last three counters instrument the retention path only; zero in the ordinary
path means uninstrumented, not zero drawing. Matching a prefix does not guarantee
the whole item was reused: a later mismatch can replay that prefix. Owner clears
and stream rewrite clears are separate calls. Retention replaces many clears
and submissions but still hides and reveals active retained items every frame.
Removing that visibility churn needs an end-of-frame lifecycle that hides unused
items safely; simply omitting the calls can leave stale sprites visible.

Corrected-cohort atlas updates were bank 36 off / 81 on, docks 43 off / 70 on.
The first cohort was bank 77/36 and docks 73/43. Each existing
2048×2048 RGBA page update submits 16 MiB. Total submitted bytes were bank
604/1,359 MB and docks 721/1,174 MB (decimal, rounded) in the corrected cohort.
Art and animation dominate.
These are CPU API submission volumes, not measured PCIe traffic or GPU timings.
Retention does not change atlas allocation policy; unequal uploads are a workload
confounder, not a claimed retention optimization.

## Pixel and bounded-recovery checks

The corrected Release binary passed 794 batcher checks (the first had 769).
Recovery coverage uses an
exact ordered-command oracle over 8,192 commands, 8,193 commands, repeated
overflow, shrink to three, unchanged small stream, empty stream and repeated
empty stream. Owner-clear/reset paths and bounded storage are checked.

Direct same-client off/on captures at bank, forest, dungeon and docks found zero
pixel differences. Each capture waits for at least two actual client draws;
both on captures must show compared and matched records, and off captures show
neither. This passed again on the corrected binary, with all 3,686,400 world
pixels stable and equal per scene. This verifies stationary captured frames, not every live roof, lighting,
picking, transparency, camera or device combination. Short parity-window timings
were mixed: on/off means were bank 11.305/11.222 ms, forest 5.955/7.608, dungeon
5.717/5.328 and docks 11.036/9.919. Keep this contrary evidence.

## Allocation profile and next bounded changes

A separate 30-second `gc-verbose` trace of the preceding frozen Release build
at bank, retention off, contains 420 weighted allocation samples totaling
44,772,816 bytes, with zero lost events and zero missing allocation stacks.
About 88.3% of sampled allocated bytes have UI drawing stacks; 4.0% are the
cover-run expiry predicate. These are weighted allocation samples, not a complete
allocation census or CPU-time percentages. Trace overhead is excluded from FPS
measurements. The corresponding docks trace contains 550 samples totaling
58,630,856 weighted bytes, also with zero lost events or missing allocation
stacks. UI drawing accounts for 89.1% and cover expiry 3.8%. Classification uses
the `UIManager.Draw` and `CoverRuns.NextFrame` stack frames; UI updates are not
silently counted as drawing. Both traces identify `Control.AddToRenderLists`,
Button, GumpPic and GumpPicTiled as leading allocation sites.

Concrete source targets are the debug rectangle closure allocated even while
debug drawing is disabled, per-frame UI callbacks and nested RenderLists, and
the per-bucket cover expiry predicate. Preserve deferred control dimensions,
texture lifetime, callback order and scissor behavior when replacing callbacks.
Start with the debug guard and expiry scratch reuse as a separate change, then
measure the remaining UI callbacks before expanding scope.

Smaller atlas pages or a dedicated frequently updated texture remain separate
experiments. Native bulk submission/MultiMesh remains a later candidate; these
measurements do not establish that moving game logic to GDScript or changing
Godot itself would help.

Private evidence run: `20261024_guo-codex-perf-implementation-20261022_retained-travel-attribution`.
Raw captures, traces and profiles must not be published with this report.

## Final validation and review status

794 actual Release checks and 1,133 optimized Debug/Labs checks passed. The
normal full smoke finished with aggregate `OK`; its inherited editor assertion
`splat_lods_present` still reports failure and is not resolved by this work.
Docs lint (320 documents), launcher lint (114 pairs), privacy scan and diff
whitespace checks passed. Owned sampling clients exited and their cleanup
receipts are retained. The current branch is a local review candidate; no public
push, merge, default toggle change or blanket visual-parity claim is implied.
