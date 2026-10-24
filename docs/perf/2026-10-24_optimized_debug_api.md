# Private optimized Debug Godot API comparison

Date: 2026-10-24. Layer: private diagnostic for the existing L4 RD experiment.

The matched Debug binding experiment is now built, loaded and measured. Both API sets passed 1,557 checks and exact four-scene canvas/RD pixels. This is a private stock-editor comparison, not a new exported Release result or permission to enable RD by default.

## Controlled artifacts

GUO source is the unchanged `6655050e` checkpoint. The fixed optimized Debug/Labs GUO DLL SHA256 is `cc058d11e81f9ca76a4df52eaca48d0595a666d541609715c05bd4639f3738f6`. Each run hashes it before and after exit. No shader, art, sort, picking, export guard or configured engine changed.

The two disposable editors contain identical stock native bytes: SHA256 `45336315eb6f1a52a8923bc4f2ce8079a03dc4939dcb7d531047890f1f7cdfab`. All 73 donor files outside `GodotSharp/Api/Debug` match in both copies. The self-contained editor marker keeps editor state private. Release API and GodotTools are unchanged.

Matching glue was generated once by that actual editor executable from pinned Godot source `ed1daf0bf001b61586d9930840f2f1394092c079`. Core/editor native API hashes are `3677103882` / `1629494564`, identical to the donor. Both cohorts use this same generated source. SDK 10.0.301 / net8.0 reference pack 8.0.28 built all four managed projects with `DEBUG` and `TRACE`; compiler flags are `/optimize-` versus `/optimize+`. Package generation, local push and global cache removal hooks stayed disabled; caches and outputs are private.

| Core API | MVID | SHA256 |
|---|---|---|
| Normal Debug | `2a3ebb5e-6d87-4b3d-bd08-61c95ef57b00` | `2cdd261a9f3aa49c2053eb02f9bfc05196ab49fafa36ca6d4adfd6537767586d` |
| Optimized Debug | `fc20dae3-e2fe-40eb-84c5-6745155d5b70` | `2f5c048cdeafe23657c27233a0e8a876fec184e60a23657cc27a401e8811625a` |

Every live parity/timing report joins the actual loaded core MVID and Debuggable optimization flag to its immutable offline file receipt, and confirms a Debug engine plus optimized GUO. All staged Debug API files and the native EXE are rehashed after exit. Editor/plugin companion files were matched on disk; the existing runtime fields establish the loaded core identity, not that both companions were loaded. Compiler attributes are not proof of every method's eventual JIT tier.

## Parity and validation

Both cohorts passed all 1,557 batcher checks, including 339 ordered-RD checks covering layout, order, alpha, fallback/recovery and residency. Both logged one CanvasItem and one ObjectDB instance leaked at validation exit; the live parity runs logged two CanvasItems/four ObjectDB instances. These warnings are retained rather than treated as a clean shutdown.

At 2560x1440, zoom 2, plain preset and PostFX off, bank, forest, dungeon and docks have zero stable-pixel and zero strict pixel differences between canvas and RD. Both RD captures per world scene actually used the pass; sampled parity runs have zero fallback. Login is a separate 640x480 pre-world capture. All three plain captures were stable in every scene. Across the normal/optimized launches the first plain captures also differ by zero pixels, without masks or excluded rectangles. Saved bank RD artwork was visually inspected.

This successful pair does not resolve the earlier repeatable cross-launch terrain variation. Those old raw differences and their unresolved explanation remain in the checkpoint report. Static scenes exclude dynamic world objects, not weather; this is not complete live-gameplay parity.

## Sustained GUO-only samples

Live world, 2560x1440, zoom 2, plain preset, PostFX and retention off. Each world scene has a 60-second warmup followed by a 30-second time-based recording. The existing probe disables vsync/engine pacing during measurement; its `--no-focus` mode counts as active. Unlike the separate exported fixture, this editor probe is uncapped. No CUO benchmark or computer-use automation ran.

| Scene | Path | Debug API | Draws / seconds | FPS | p95 ms | Prepare ms | Managed draw ms | KB/frame | Host CPU mean* |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| britain-bank | canvas | normal | 2402 / 30.007832 | 80.046 | 18.883 | 4.515 | 4.311 | 5.4 | 39.7% |
| britain-bank | canvas | optimized | 2776 / 30.003640 | 92.522 | 15.575 | 4.441 | 3.121 | 5.4 | 43.4% |
| britain-bank | rd | normal | 1607 / 30.014475 | 53.541 | 26.146 | 6.449 | 9.948 | 6.4 | 50.2% |
| britain-bank | rd | optimized | 2320 / 30.014781 | 77.295 | 19.024 | 5.649 | 5.553 | 6.5 | 54.9% |
| britain-docks | canvas | normal | 3205 / 30.000217 | 106.833 | 13.038 | 2.975 | 3.267 | 5.7 | 29.2% |
| britain-docks | canvas | optimized | 3587 / 30.005642 | 119.544 | 12.871 | 2.940 | 2.482 | 5.4 | 32.7% |
| britain-docks | rd | normal | 2375 / 30.003612 | 79.157 | 17.204 | 4.178 | 6.913 | 7.7 | 47.9% |
| britain-docks | rd | optimized | 3477 / 30.001334 | 115.895 | 13.666 | 3.495 | 3.790 | 7.5 | 38.9% |

*Host mean is over the entire launch, including warmup, not an aligned per-frame contention measure. Raw half-second host samples and all outcomes are retained. Other project workloads were allowed to run. No own compile overlapped these timing windows.

Each report has at least 30 seconds, at least 0.95 draws/process-frame, no sample-storage growth, dropped interval or interval-thread change. All eight draw counts and elapsed durations independently reconcile exactly to their per-second intervals. RD samples have all measured frames on RD and zero fallback; canvas samples have zero RD frames. Owned process cleanup and unchanged DLL/API/native hashes pass.

Run order was bank normal-canvas, optimized-canvas, optimized-RD, normal-RD; docks normal-RD, optimized-RD, optimized-canvas, normal-canvas. This is one sustained window per cell, not a randomized or isolated-host causal estimate. Atlas updates, dynamic objects/weather and shared load vary. Do not compare these numbers directly with prior exported Release cohorts.

## Interpretation

- britain-bank, canvas: normal/optimized API 80.046/92.522 FPS (+15.6% observed), managed draw 4.311/3.121 ms. This percentage is an observation under shared load.
- britain-bank, rd: normal/optimized API 53.541/77.295 FPS (+44.4% observed), managed draw 9.948/5.553 ms. This percentage is an observation under shared load.
- britain-docks, canvas: normal/optimized API 106.833/119.544 FPS (+11.9% observed), managed draw 3.267/2.482 ms. This percentage is an observation under shared load.
- britain-docks, rd: normal/optimized API 79.157/115.895 FPS (+46.4% observed), managed draw 6.913/3.790 ms. This percentage is an observation under shared load.

The normal Debug API materially changes the cost of this prototype and must be identified in future comparisons. The ordinary exported Release client already loads optimized Release bindings, so these observations do not establish a new production FPS gain. The whole frame remains above the 4 ms budget in these bank/docks windows; no 250 FPS claim follows. RD still performs CPU world preparation and ordered capture; a small GPU draw count is not a performance result.

Keep both GPU experiments off by default and keep the RD implementation Debug/Labs-only. This experiment grants no Labs export, configured API replacement or default promotion.

## Reproduction and evidence

The six fresh restart attempts are retained under the private Temp `guo-debug-api-restart-20261024-attempt*` folders. Attempts 1–3 exposed missing IL conversion handling/unsigned extension in the offline receipt, attempt 4 exposed a UTF-8 BOM rewrite problem, and attempt 5 hit sandboxed Windows SDK discovery. Attempt 6 built both matched sets. Failed directories were never reused or removed. These fixes affect disposable diagnostic scripts only.

Build and runtime drivers: Temp `guo-debug-api-restart-prep/build_debug_api.py`, `guo-api-command.py`, `guo-api-restart-run.py`, `guo-api-cohort.py`, `guo-api-analysis.py`. `verified-build-receipt.json` includes compiler assertions, staged identities and unchanged donor files. Raw reports are in the configured external build root under `perf/perf_api-*.json` and `guo-sustained/api-*/`.

Central private evidence run: `D:/_agentShared/runs/guo/20261024_guo-codex-perf-implementation-20261022_optimized-debug-api/`. Source/measurement and original PNG archives are indexed there. Archives exclude credentials, profiles/settings, DLLs and raw binary traces. The local artifacts are not published. No push or merge was performed.

See also [the restart checkpoint](2026-10-24_native_render_attribution.md) and [consolidated review](reviews/2026-10-24_fable_performance_review.md).

## Final checks and archive identity

Current-checkpoint full smoke passed (`[smoke] OK`, 186.218 seconds), including editor checks, after the API cohort ended and before native recording. Shutdown warnings remain in the raw logs. Source/measurement archive SHA256: `f19c2eab456f47e74e15a273923002c0c7d5e45729f917d056f8825c91c000b1`; original PNG archive: `1407bfa3c8684d4e68e11197c72286b210e21829a0de713561149034249ec518`. Selected text passed the configured-credential scan.
