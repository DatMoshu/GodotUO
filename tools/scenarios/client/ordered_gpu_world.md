# client.renderer.ordered-rd — complete ordered GPU world

Requires an optimized Debug `GuoLabs=true` build and an RD renderer. No Labs
code is included in exports. See ADR-0043 and the feature README for the doctor.

1. Run `godot-console --path godot/GUO -- --batcher-probe`. Require PASS for
   both persistent terrain and ordered RenderingDevice stages. Synthetic art
   covers hue modes, overlapping alpha, slope normals, atlas texels at half
   scale, fractional transforms, empty frames, resize and forced fallback
   before upload and after command recording. It also skips native pre-draw
   between two captures, reuses the canvas pool, then requires current-frame
   pixels and backend counts when resuming either canvas or RD.
   Resident cases require zero writes on an unchanged frame, exactly one
   dirty quad for a single addition, order-only alpha reversal, storage growth,
   identical-ID recovery after partial geometry/order failure and resize.
2. Use the authorized GM account and scratch profile to run `tools/perf_probe`
   with `--as-built`, `--perf-profile plain --perf-static-world --perf-parity
   --rd-world`, matching size and zoom. Measure bank, forest, dungeon and docks.
   Require zero repeatable pixel differences and `rd_test_on_pass == 2` for
   every world scene. Inspect non-repeatable differences separately.
3. Repeat with moving actors and with fractional zoom. Exercise the runtime
   toggle, resize, relog, circle transparency and scene unloading. Custom
   blends/clips, styled water and HD art must preserve the complete canvas
   image when unsupported. Never interpret fallback as proof of RD parity.
4. Build without Labs and run the normal smoke and deterministic performance
   gate. Compare identical frozen inputs for default-state parity.
5. Benchmark only after correctness, under an exclusive host timing lease.
   Compare the same CPU contexts against optimized CUO and report RD recording
   CPU separately. Record whole-frame p95/p99, allocation, actual viewport,
   backend, uploaded bytes and RD draw calls. Godot's canvas counters omit
   custom RD draws; a lower canvas counter is not a GPU performance result.

Save private logs and captures outside the checkout in the shared GUO run.
Mark unexecuted live steps NOT RUN; the synthetic probe is not full UO acceptance.
