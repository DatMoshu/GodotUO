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
   Pool controls enlarge a visible canvas frame by 256 items, then require
   identical small-frame pixels, backend counts and native clear counts across
   repeated RD frames, toggle-off, early rejection with a large native suffix,
   and a canceled capture. The idle tail must be cleared once, not every frame.
   Ordered-transform controls require one draw for adjacent equal textures with
   different exact transforms, one for A/B/A with preserved alpha order, and a
   failing pixel oracle when a probe-only fault deliberately omits the absolute
   offset in a later batch with more than four textures on active RD.
   Require zero unchanged state writes; one full active-stream write for one,
   distant or camera changes; brightness/radius pixel changes without state
   writes; state-only capacity growth; shrink/empty/regrow; resize; identical
   retries after failures before/after upload; and canceled-ticket replacement.
   Four-texture controls require exact different-size/edge-UV sampling, correct
   fifth-view/repeated flushes, a visibly failing wrong-slot oracle, invalid-slot
   fallback, selector-only state updates without geometry/order writes, palette
   replacement, complete post-record replay, resize and canceled bindings.
   Require the emitted shader layout/literal-binding contract check to pass.
   CPU packing must match every byte of the former 48-byte packet across the
   IEEE and hue/alpha boundary fixture, and its changed-bit negative control
   must fail equality. Confirm the 16-byte transport / 48-byte vertex ABI.
   Repeat mesh fallback and half-alpha pixel controls so reusing encoded colors
   cannot accidentally quantize the original replay colors twice.
2. Use the authorized GM account and scratch profile to run `tools/perf_probe`
   with `--as-built`, `--perf-profile plain --perf-static-world --perf-parity
   --rd-world`, matching size and zoom. Measure bank, forest, dungeon and docks.
   Require zero repeatable pixel differences and `rd_test_on_pass == 2` for
   every world scene. Inspect non-repeatable differences separately.
   Retain all five files listed by `parity_frames`; recompute the stable mask
   from the three plain frames. Check each test independently against that mask,
   in addition to repeatable strict differences shared by both test frames.
   With sample window version 2, an uninterrupted RD scene must report exactly
   the measured `frames` in `rd_frames_drawn`, without the former extra interval.
3. Repeat with moving actors and with fractional zoom. Exercise the runtime
   toggle, resize, relog, circle transparency and scene unloading. Custom
   blends/clips, styled water and HD art must preserve the complete canvas
   image when unsupported. Never interpret fallback as proof of RD parity.
4. Build without Labs and run the normal smoke and deterministic performance
   gate. Compare identical frozen inputs for default-state parity.
5. Benchmark only after correctness. Prefer an exclusive host timing lease;
   when the owner authorizes measurements alongside other projects, use an
   ordinary runtime lease, preserve their work and label every timing as
   shared-load evidence. Do not claim a clean speedup from those timings.
   Compare the same CPU contexts against optimized CUO and report RD recording
   CPU separately. Record whole-frame p95/p99, allocation, actual viewport,
   backend, uploaded bytes and RD draw calls. Godot's canvas counters omit
   custom RD draws; a lower canvas counter is not a GPU performance result.

Save private logs and captures outside the checkout in the shared GUO run.
Mark unexecuted live steps NOT RUN; the synthetic probe is not full UO acceptance.
