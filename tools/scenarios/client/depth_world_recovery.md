# Production Depth World recovery

Layer: L0 presenter under ADR-0044. Ordinary non-Labs RD selects it; use
`--canvas-world` or Compatibility for canvas. No account or shard is needed.
Run at 1280x720 in an isolated external source/build/profile stage with a
project Godot runtime lease. Set engine/upstream/client-data paths through
existing configuration. Never install diagnostic C# in the delivered assembly.

`depth_world_recovery.cs.fixture` extends BatcherProbe with
`VerifyResidentRecovery(batcher, viewport, palette)`. Copy it as C# into the
stage, invoke that method in BatcherProbe.Verify after viewport/batcher setup,
and return from Verify. Set `GUO_RECOVERY_FIXTURE_DIR` to an existing evidence
directory, build optimized Debug with `GuoLabs=false`, and run
`godot-console --path <stage-project> --rendering-method forward_plus --
--batcher-probe --depth-world --silent`. Require 40 checks/0 failures and
`results.json`: early admission, middle resident admission/unvisited suffix,
before-upload, after-recording, clip rejection/raw suffix, successful resident,
and equal-depth ties in both traversal directions under early/late failure.
Both dynamic objects must draw exactly once. Clean teardown must produce no
CanvasItem/RD resource warnings.

`depth_world_real_recovery.cs.fixture` extends the offline zoom fixture. Copy
both into the stage and call `await RealRecovery.Run(this, scene, dir)` after
the ordinary full-walk comparisons. The fixture freezes gameplay/time updates,
compares two canvas controls, exercises a successful resident frame, and compares
early/middle/upload/after-recording recovery at1,2.5,5x. Require zero differing
bytes, actual resident success, no cold recovery plan on success, and nonzero
mesh/flat covering-land counts in the late cases. This exercises the actual
Britain map and art in place; captures remain private. The cold path must match
canvas, including its own documented terrain approximations.

For manual/Compatibility selection, skip only the RD-specific mask/recovery
calls, retain the offline zoom/full-walk/wheel checks and screenshots, and require
DepthWorld enabled=false, FrameAdmitted=false and zero RD frames. Run once with
`--canvas-world` on RD and once with `--rendering-method gl_compatibility`.

Re-stage the original Main/BatcherProbe and remove injected diagnostic C# before
the required `launchers/dev/smoke.bat` and release export. Run the normal Release
payload separately; compilation or a fixture build alone is not export proof.
Keep exact source/build hashes with the run. These checks make no live mobile,
full CUO parity, stable FPS, or remote Dell claim.

### Canvas reuse bookkeeping (D1)

The map-backed fixture also records per-frame `ChunkWalkRecords` and `ChunkZScanObjects` in `chunk-maintenance.json`. Every canvas frame must report zero for both, and an RD-to-canvas return must exactly match the frozen canvas pixels at 1/2.5/5x. The ordinary object walk, stale screen-position refresh and dirty-tile clearing still run. An admitted RD frame rebuilds its cache after canvas; no stale key/light list can be reused. For Compatibility, skip the RD-only mask/recovery phases and check canvas repeat controls and zero bookkeeping. These are work counts, not FPS evidence.

### Resident texture-binding scratch (H4)

The device reuses one RID scratch array only within serialized RenderRT callbacks; DrawListEnd consumes the active prefix before another callback can rewrite it. No frame ticket retains the array. Growth is geometric and DisposeRT releases the storage. The real-map fixture asserts no ResidentScratchGrowths increase on repeated stable resident draws after canvas/RD transitions at each zoom. All12 exact-pixel recovery arms still execute. The observed growth count is allocation work, not GPU traffic or an FPS result.

### Morning comparison identity

PerfRuntimeIdentity effective_flags now identifies the selected Depth World presenter and chunk reuse independently of the Godot backend. Each PerfProbe scene records renderer_state after the measured interval and before pacing restoration: actual world/light/UI target sizes, final resident admission, presenter/reuse selection, Engine.MaxFps, VSync and active state. This diagnostic snapshot allocates only after timing stops. RD versus manual canvas can share forward_plus; validate selection plus nonzero resident/drawn counters and zero unexpected fallbacks. Compatibility remains a separate cohort. The legacy perf_cohort arm schema is unchanged and is not sufficient alone for this comparison.
