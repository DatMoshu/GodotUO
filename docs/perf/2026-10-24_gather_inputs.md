# Shared inputs during world preparation

Status: local review candidate; no isolated speedup established. Parent `b2fa6237`.
Layer: L0 implementation optimization, ordinary renderer and existing mesh
paths. No new player option, shader or render semantics. The GPU experiments
remain off by default and separate from actual Release measurements.

## Change and lifetime

The synchronous world gather previously reread view range, player height,
range/death coloring flags, CoT enablement and mouse-circle geometry for each
meshed object. A readonly value struct now captures those shared values once
after viewport and object-handle updates, immediately before the chunk walk.
It is passed explicitly and is never retained on a scene or object. GPU
fallback performs a new gather and therefore takes a new snapshot.

Object hue, distance, partial hue, wet flags, height, roof/visibility/alpha
tests, light offers and picking stay live, in the same order. Highlight
restoration outside the gather uses fresh inputs. The existing in-game
guard and main-thread, non-reentrant gather contract provide the lifetime.
This removes repeated reads/calculations, not the tile walk or object tests.

`GUO_VERIFY_GATHER_INPUTS=1` compares the captured values with current live
values at mesh hue/CoT uses and fails on a mismatch. The diagnostic is off
for timing because it intentionally repeats the old work. Its cumulative
counters include fresh highlight-restoration captures; they are not an
object count or proof of every possible gameplay transition.

An independent source review found no concrete correctness defect. It
identified a verifier gap before an alternate gradient-CoT early return;
an additional check now precedes that branch's first snapshot use. This
diagnostic-only addition follows the initial v1 export.

## Measurement boundaries

Use actual exported Release engine and optimized Release bindings, with
GPU-world/RD and canvas retention off to isolate this change. Fixed live
locations are bank 1434 1699 0 and docks 1497 1790 -5, camera 2560×1440,
zoom 2, cap 250, classic lighting and PostFX off. Following Fable's review,
discard 60 seconds after arrival, then sample 30 seconds. Earlier 15-second
settles remain in the retention report, separately. All observations are
under shared host load; no CUO
process is launched. Full-frame throughput is drawn frames / elapsed time.

Frozen baseline DLL:
`a2530e68d8010d3c5731d99289f3347d833b9ca926a6efaecf0cdb98a1af5078`.
It includes the new sustained allocation fields but no gather-input change.
Initial v1 candidate DLL:
`b8e97dc31adede999b0a3d90afd458f1eabcc1cdc27efeebf71472fbb8d48312`.
Final v2 candidate DLL (additional verifier check):
`7b7cf712f49a57798d89f9f300a8052cbde5569dde40c4d340075a647ca2368f`.

## Validation

Both exported Release candidates passed 573 batcher checks, including hue,
land lighting, persistent terrain, atlas metadata, cache identity and
invalidation, retained commands, gump transforms/clipping and fonts.
The final optimized Debug/Labs build passed 912 checks, including the 339
ordered RenderingDevice checks; this is compatibility coverage, separate
from the actual Release timings.

Cross-build frozen captures compare three stable plain frames per build.
Forest, dungeon and docks each matched all 3,686,400 pixels. Bank had 2,599
different pixels, all in the server-message HUD rectangle x=2..280,
y=1306..1337: the later login displayed "You have left the protection of the
town guards." The world pixels matched. Raw captures, failure result and
the inspected crop are retained, not silently masked. Each build's existing
same-client batched/plain comparison also passed in all four scenes.

The v1 verifier made 81,841,792 cumulative checks through the stationary
scene suite, with zero mismatches. Separate live bank/docks/crypt/walk runs
enabled range tint plus binary CoT (type 0) and gradient CoT (type 1).
The private profile inputs were checked for those exact settings. Each walk
moved 46 tiles in 12 seconds; cumulative check counts were 81,018,943 and
76,762,538, respectively, with zero mismatches. A final v2 gradient-CoT
crypt/walk repeat moved 46 tiles with 53,288,960 cumulative checks and zero
mismatches. The diagnostic runs are not performance measurements.

These checks establish the tested invariant during loading, motion and
stationary scenes. They do not independently prove every mouse-boundary,
selection identity, death transition, dynamic lighting receiver or old
instanced-GPU fallback case. Source review preserves those decisions, but
broader acceptance remains separate. No device acceptance is claimed.

The required full smoke command returned exit 0 and `OK`. It retains an
existing editor Art advisory: 100/101 checks with `splat_lods_present`
reported failed although the editor aggregate returns success. The same
advisory appears in the preceding static and retained-canvas smoke logs;
it is not a new gather regression. The director was notified of this gate
blind spot. Four sampler tests, docs lint, all 114 launcher pairs, privacy
scan and whitespace checks passed.

## Sustained actual Release observations

Order: baseline bank, candidate bank, candidate docks, baseline docks.
Each window contains 29 observations over about 30.1–30.3 seconds, after
the 60-second discard. The invariant verifier and retained-canvas experiment
were disabled for every timing window. No local build or pixel capture ran
during these windows; other projects remained active.

| Scene | Baseline FPS | Candidate FPS | Baseline / candidate CPU maximum |
|---|---:|---:|---:|
| Bank | 71.285 | 74.058 | 96.1% / 99.4% |
| Docks | 121.416 | 123.827 | 99.6% / 100.0% |

The differences are too small and the competing load too variable to
establish a causal gain. The host maxima cover each full launch/warm-up/run,
not just the final sampling interval. One-second bank rates ranged 47–95
baseline and 50–116 candidate; docks 70–162 and 76–163. Do not turn these
numbers into an improvement percentage or a clean-machine 250 FPS claim.

| Scene | Baseline / candidate prepare ms | Baseline / candidate draw ms | Approximate bytes per draw |
|---|---:|---:|---:|
| Bank | 5.910 / 6.015 | 4.494 / 4.188 | 20,770 / 20,527 |
| Docks | 3.877 / 3.690 | 2.282 / 2.070 | 20,126 / 19,761 |

Phase numbers average rolling profiler windows. Bank preparation did not
improve in this observation, despite the small throughput increase. Bank
had no collections in either sample. Docks had two generation-0 and one
generation-1 collections before, versus one generation-0 after; neither had
generation-2 collections. These are whole-client observations, not causal
allocation attribution or retained heap measurements.

Keep this candidate local for review. Prioritize Fable's measured atlas
upload/walk diagnostics and runtime phase attribution next, rather than
inferring a successful preparation optimization from noisy FPS alone.

Private evidence run:
`20261024_guo-codex-perf-implementation-20261022_gather-inputs`.
No public push or merge; no new default GPU/retention toggle change.
