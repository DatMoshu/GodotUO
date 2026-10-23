# Ordered GPU world experiment

L4, off by default, Debug only; see ADR-0043. This tests whether bulk world
submission through Godot's RenderingDevice reduces CPU submission cost while
preserving the original UO pixel pipeline. Gameplay and the CPU painter order
remain the reference. No experimental code is included in exports.

Build with `dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true
-p:Optimize=true --no-incremental`. Enable with `--rd-world`, the `-rdworld`
chat command, or the shared Labs Options action. The toggle is session-only.
Do not combine it with the older `--gpu-world` experiment.

The CPU retains the ordinary gameplay traversal and painter order. Four
corners per quad are cached in a persistent storage buffer; the GPU expands
them into triangles and applies the same camera, hues, normals and alpha blend.
Canvas rectangles and meshes retain their distinct triangle diagonals. Affine
sprites retain the canvas model transform. The complete world target starts
opaque black, avoiding another translucent blend/rounding boundary.

Accepted world commands now skip ordinary canvas primitives. At FramePreDraw,
the render-thread callback records RD commands and shows exactly one current
frame branch. A low-level replay plan retains exact rectangle coordinates,
affine transforms, original mesh colors and normals. An unsupported command
replays the accepted prefix before the ordinary suffix; a late recording failure
replays the complete plan. Gameplay drawing is never invoked twice. Private
replay children are hidden before pooled parents are reused, including toggle
off and skipped native frames. Each child explicitly uses nearest sampling.
Unsupported materials, clips, HD art, styled water, shared material changes,
batched-world mode and separate rendering threads use canvas. This initial
deferred replay implementation requires the caller to be on the rendering thread.
Initialization and resize preserve the presenter RID;
retired source textures remain alive until its dependent view is replaced.
An unsubmitted capture is cancelled before canvas-item pool reuse when a
native draw is skipped. Data for an already submitted callback stays intact.
There is no GPU readback, Submit/Sync call or intentional frame of latency.
This remains a correctness prototype, not a claim of faster rendering.
`rd_deferred_quads_per_frame` and `rd_canvas_replay_quads_per_frame` expose the
removed and fallback primitive work; empty canvas containers still exist.
Godot's normal draw-call and
GPU-time monitors do not include this custom RD pass. Use the explicit RD
draw/upload/recording counters and whole-frame measurements.

Dependency doctor: the repository's pinned mono Godot, .NET SDK and an RD
backend (Forward+ or Mobile). No extension, Python runtime, model or service
is required in the client. Compatibility uses canvas. If the toggle is absent,
repeat the Debug Labs build command above; exports deliberately exclude it.

Smoke/pixel doctor: `godot-console --path godot/GUO -- --batcher-probe` after
that build. Requires a real GPU, not `--headless`. The normal smoke command
still applies. Scenario: `tools/scenarios/client/ordered_gpu_world.md`.
Report live parity and speed separately; a passing fallback is not active RD
parity. Stable world-object identity and GPU culling remain subsequent work.

Resident geometry uses an exact-content cache of at most 65,536 quad records.
Only changed contiguous slot ranges are uploaded. A separate ordered slot-ID
stream preserves every occurrence, including repeated translucent geometry;
reordering does not rebuild geometry. Hashes narrow candidates, and all 192
record bytes decide equality. Existing hits are pinned before misses allocate
or evict, so an early miss cannot overwrite a later reference in the same frame.
Handles are private to the current frame, not persistent gameplay identifiers.
Buffer growth/resize invalidates the affected GPU contents; failed partial
updates invalidate both streams for retry, even when the next IDs are identical.
Capacity refusal falls back to the complete current canvas frame.

This removes redundant GPU transfers, not the CPU capture/hash traversal.
Terrain retains world-space geometry while ordinary sprite positions can
change with camera movement. New animation/alpha/hue variants consume cache
entries until eviction. The bounded high-water cache does not shrink on camera
movement. Geometry storage is at most 16 MiB after power-of-two growth; CPU
records, dictionary keys and ordered occurrence arrays also consume memory.
Atlas/texture bindings are resolved each frame; geometry residency does not
claim atlas lifetime or device-loss recovery.

`rd_geometry_bytes_per_frame`, `rd_order_bytes_per_frame`,
`rd_upload_calls_per_frame`, `rd_dirty_quads_per_frame`, `rd_cached_quads`,
`rd_peak_cached_quads`, `rd_buffer_growths` and `rd_cache_evictions` distinguish
data reuse from draw-call reduction. Upload counters include successful writes
before a later transaction failure. The 136-check synthetic RD stage covers
unchanged zero-upload frames, one dirty quad, order-only alpha reversal, growth,
partial-upload retry and resize with identical IDs, as well as replay parity.

The initial PC experiment passed 54 synthetic RD checks and four static-world
scene comparisons at 2560x1440, zoom 2, with both captures on RD and zero
repeatable or stable-pixel differences. It was slower than canvas in all four
paired scenes. This establishes a tested pixel/material boundary, not full
gameplay acceptance or a performance win. Dynamic live parity, fractional-zoom
world scenes and device RD validation remain outstanding. Preserve the toggle
off; the exported terrain optimization does not depend on this experiment.

`rd_record_cpu_ms` covers the render callback, including initialization and
fallback handling, but excludes capture, queued waiting and GPU execution.
Draw counters count successfully completed recording transactions, including
an injected failure afterward. They are diagnostic counters, not GPU timers.
