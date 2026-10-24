# Native rendering attribution, version 3

Date: 2026-10-24. Private, default-disabled diagnostic; no configured engine or GUO source change.

The prepared version-3 engine is now built and the bank/docks wall-only windows are complete. These scopes split native canvas batch construction/emission and RenderingDevice end-frame work. They measure inclusive elapsed time on the executing thread, not GPU duration, exclusive CPU or blocked/ready states.

## Artifact and fixture identity

Pinned Godot base `ed1daf0bf001b61586d9930840f2f1394092c079`; same effective production Mono template flags as the preserved private original: MSVC 14.44.35207, SDK 10.0.26100.0, x64, single precision, `/O2 /MT /fp:strict /Zi /DEBUG:FULL`, no LTO or feature stripping. The incremental private build completed in 82.141 seconds, with four jobs and a ten-minute external bound. All seven source-manifest hashes matched before/after; original artifacts and v2 evidence remain preserved.

- Native EXE SHA256: `91a3baaa27178be161fa0a11092b65325570e63fbbf3c4d5b0eb65408deafaa2`.
- PDB SHA256: `3e555ff78cc95f37dedd7679a80fae108aff9e9dc26c43b24a6c090c3fc1ab2b`.
- PE/PDB GUID `AFD2E985-A8A3-4992-995D-37E361E957E6`, age 4; independent DIA symbol validation passes. V2 was age 3; its PDB must not be substituted.
- The copied frozen export has 200 verified files; only `GUO.exe` differs from `windows-cover-membership-v1`. Its GUO DLL remains SHA256 `449854e5da5e3d3a217cd42bd0f0dcacfa8c93701217e2381f8fac87c789658b`, the same managed cohort as native-v2. It is not the newer lazy-Land DLL or the private Debug/Labs API experiment.

231 isolated native assertions pass, plus seven real-clock/thread/serialization utility cases and seven no-overwrite checks. The fixed caps remain eight thread lifetimes, 64 scopes/draw and depth 16; work counters cover 12 stages. The frozen Release export passes all 1,207 batcher checks. Current checkpoint full smoke also passes, including editor checks; it completed before native timing. No own compile overlaps either native recording window.

## Pixel controls

With native profiling disabled and then enabled, the existing same-client plain/batched checks have zero stable and strict differences in bank, forest, dungeon and docks. Cross-launch first-plain raw differences are zero in login/bank/forest/dungeon and 261 in docks, bounded by `(1273,688)-(1287,719)` at the center actor position; none are stable across the three plain frames. Both full docks images were inspected. Original PNGs and the unmasked raw comparison are retained. This does not resolve the older repeatable terrain variation or establish a new strict v2/v3 cross-engine pixel comparison.

The enabled pixel-control profile intentionally stops before its 300-second deadline and is a partial diagnostic, not an accepted performance recording. It is distinct from the complete delayed windows below.

## Complete wall-only windows

Both ordinary canvas fixtures use 2560x1440, zoom 2, PostFX/retention/GPU experiments off and live world contents. Profiling starts 180 seconds after initialization and runs 30 seconds. Setup sampling finishes at least 60 seconds before profiling. The driver verifies exact PID, thread creation identities, schema 3, all 12 stages, zero clock/nesting/outside-draw/incomplete/overflow errors, complete footer and no active scopes at write. Every count, wall total and work-count field reconciles to the end-second buckets; maxima reconcile as well.

| Window | UTC start | Native draws | Host CPU mean / max in window | Empty guard us |
|---|---|---:|---:|---:|
| britain-bank | 2026-10-24T10:43:44.377367+00:00 | 3406 | 21.4% / 39.4% | 0.080 |
| britain-docks | 2026-10-24T10:47:15.680656+00:00 | 4324 | 26.8% / 64.1% | 0.084 |

Bank discarded two scopes crossing the recording end; docks discarded none. Accepted child-scope counts can therefore include a boundary fragment not represented by a complete outer draw, as shown by the small bank calls/draw deviations. Native draw counts describe accepted executed-draw scopes, not an independently aligned game FPS sample. Whole-frame setup samples are separately labeled `pre-native-window-only`. Host contention remains present; these are not controlled instrumentation-overhead comparisons. Empty-guard cost is reported, never subtracted.

| Inclusive elapsed ms per executed draw | Bank | Docks | Bank / docks calls per draw |
|---|---:|---:|---:|
| `executed_draw` | 2.671 | 2.353 | 1.000 / 1.000 |
| `canvas_update` | 0.000 | 0.000 | 1.000 / 1.000 |
| `cull_tree_inclusive` | 1.701 | 1.470 | 8.001 / 8.000 |
| `rd_canvas` | 1.167 | 1.022 | 8.001 / 8.000 |
| `canvas_batch_record` | 0.481 | 0.419 | 4.001 / 4.000 |
| `canvas_batch_emit` | 0.630 | 0.550 | 4.001 / 4.000 |
| `rd_end` | 0.826 | 0.756 | 1.000 / 1.000 |
| `rd_transfer` | 0.029 | 0.028 | 1.000 / 1.000 |
| `rd_graph_end` | 0.796 | 0.727 | 1.000 / 1.000 |
| `rd_driver_finalize` | 0.001 | 0.001 | 1.000 / 1.000 |
| `execute_present_api` | 0.092 | 0.088 | 1.000 / 1.000 |
| `next_frame_begin` | 0.020 | 0.011 | 1.000 / 1.000 |

`cull_tree_inclusive` includes `rd_canvas`; its batch record/emit scopes are nested. `rd_end` includes transfer, graph end and driver finalization. Do not add parent and child rows as independent shares. Here RD means Godot's native RenderingDevice backend, not the Debug/Labs ordered-world experiment.

| Work per executed draw | Bank | Docks |
|---|---:|---:|
| `canvas_batch_record.items_per_draw` | 1667.043 | 1598.000 |
| `canvas_batch_emit.batches_per_draw` | 1772.608 | 1621.985 |

Record items repeat inputs across native passes. Emit batches count nonempty dispatch attempts and may include attempts that return internally. These are not unique visible sprites or actual GPU draws.

The new raw `instances` field is **not a valid instance count**. The live bank result is about 1.78 trillion encoded units/draw. Source inspection found `PUSH_DATA_INSTANCE_COUNT = 0x80000000` and `INSTANCE_COUNT_MASK = 0x7fffffff` in the pinned canvas header: non-VAO draws set that high-bit flag, while the v3 hook sums the initialized field without decoding it. The earlier preparation note describing sentinel-one behavior was wrong. The raw values still reconcile to buckets but have no instance-count interpretation; they are retained in the raw archive and excluded from the work table. This is a diagnostic semantics error, not an uninitialized read or evidence of trillions of rendered instances. Correcting it requires a separate tested hook/build/capture. Timing scopes and item/dispatch counters do not derive from that value.

## Scope of the finding

Both scenes place most measured end-frame elapsed time inside `rd_graph_end`: bank 0.796 of 0.826 ms/draw, docks 0.727 of 0.756. Transfer is about 0.029 ms and driver finalization about 0.0005 ms. This points the next diagnostic inside graph end (dependency/task wait/sort/barrier/replay), without identifying which of those operations dominates. Canvas batch recording/emission independently remain material at bank 0.481/0.630 ms and docks 0.419/0.550 ms. A managed bulk interface alone would retain these native portions.

This refines the earlier observation that retaining managed canvas commands does not eliminate native per-frame processing. It separates the batch-processing and end-frame portions without changing barriers, order, clipping, alpha, shader math or sampling. Any optimization of these paths still needs exact rendering/order/picking proof and a whole-frame GUO-only comparison. Source-level attribution alone does not justify a bulk native interface, native graph changes or default promotion of the RD prototype.

The separate [optimized Debug API comparison](2026-10-24_optimized_debug_api.md) corrects a prototype measurement confounder; it is not an exported Release speedup. Keep that cohort separate from this frozen native Release fixture.

## Evidence and continuation

Private source/build/test/EXE/PDB identity receipts: `D:/_agentShared/work/guo-native-profile-ed1daf0bf/profiling-v3/`. Raw windows: external build root `guo-sustained/native-v3-wall-v1/britain-bank` and `britain-docks`. Drivers: Temp `guo-inprocess-v3-run.py`, `guo-native-v3-restart-summary.py`, `guo-native-v3-parity.py`. Each scene has its own summary filename; neither overwrites the other.

Central private run: `D:/_agentShared/runs/guo/20261024_guo-codex-perf-implementation-20261022_optimized-debug-api/`. Source/measurement and original pixel archives are indexed there; credentials, profiles/settings, executable/PDB payloads and raw ETL/nettrace are excluded. Owned clients are cleaned up. No CUO benchmark, computer use, UAC/WPR restart, push or merge occurred.

See [native-v2 attribution](2026-10-24_native_render_attribution.md) and the [consolidated review](reviews/2026-10-24_fable_performance_review.md).

Archive SHA256: source/measurements `92eefe7d9b3009f7632db769706cd191f5efd462e3b9e257aa5f0e660451b114`; original PNGs `b7c37f9c5378b6d9778c43c4cd20609484eaa5284b7bad88c3c7b8a38382db1f`. Selected text passed the configured-credential scan. Preserved native-v2 EXE/PDB hashes were reverified unchanged.
