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
With the RD toggle enabled, the batcher clears only items used in the previous
frame. A busier frame's full active range is cleared once; its unused tail is
already empty on later frames. The allocated pool is retained for reuse. Replay
children still have their own clearing/hiding path, and toggle-off keeps ordinary
full-pool clearing. `rd_canvas_items_cleared_per_frame` counts native batcher clear
calls in both RD-on and RD-off measurements, excluding private replay children.
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
parity. Dynamic-object identity and GPU culling remain subsequent work.

Resident geometry uses an exact-content cache of at most 65,536 quad records.
Only changed contiguous slot ranges are uploaded. A separate ordered slot-ID
stream preserves every occurrence, including repeated translucent geometry;
reordering does not rebuild geometry. Hashes narrow candidates, and all 192
record bytes decide equality. Existing hits are pinned before misses allocate
or evict, so an early miss cannot overwrite a later reference in the same frame.
Handles are private to the current frame, not persistent gameplay identifiers.
Buffer growth/resize invalidates the affected GPU contents; failed partial
updates invalidate geometry, order and transform state for retry, even when the
next IDs are identical.
Capacity refusal falls back to the complete current canvas frame.

Capture encodes each corner once, retaining that original color for mesh
replay before making the GPU's UNORM8 copy. The 48-byte vertex uses plain float
transport in the optimized GUO assembly; offsets and shader interpretation stay
unchanged. Scalar positive-zero position additions retain the previous arithmetic.
An independent byte oracle compares the old Godot value expressions with the
new packet over finite, signed-zero, nonfinite and hue/alpha boundary inputs;
this supplements the full GPU and fallback pixel controls. It does not remove
the per-frame capture or establish a performance gain by itself.

Chunk statics and terrain now additionally retain CPU source records keyed by
layer/index/draw kind. Per-quad revisions invalidate writes, hue/alpha changes,
direct stretched-land writes and pooled resets. Unchanged source records skip
corner construction, color packing and full vertex hashing/comparison. Sources
marked dirty are compared once before repacking: resetting and restoring the
same final alpha retains the old immutable ID. A content change gets a new ID,
so old/new occurrences in one frame cannot alias. Visibility, order and transforms
are still captured each frame. The source cache is capped at 65,536 records;
entries unused for two frames are swept every 60 frames or at the cap. Missing
cache capacity uses ordinary exact-content capture. The cache retains texture
and layer owners until expiry or capture disposal. GPU slots keep their existing
65,536 bound and current-frame pinning rules.

`--rd-world --rd-content-cache` is a diagnostic control that keeps the old
per-frame content route. The main toggle still defaults off, and all new source
tracking code is excluded from exports. `rd_source_record_hits_per_frame`,
`rd_source_records_packed_per_frame` and `rd_source_dirty_checks_per_frame`
distinguish reuse, rebuilding and final-alpha comparisons.

Captured source occurrences now hold a packet reference instead of copying four
vertices and an ID every frame. Each packet is immutable while its frame ticket
is live. A second revision of the same source retains the earlier packet until
ticket retirement; later frames can rewrite the current packet or reuse bounded
spares. Expiration also returns packets to the spare pool. IDs remain monotonically
increasing even when packet objects are reused. Pending rendering callbacks
prevent the next capture from clearing or reusing their owners.

Dynamic draws still use per-frame CPU capture/hash traversal. Complete canvas
replay reads the ordered packet/dynamic references directly, constructing native
canvas commands only on failure; no second expanded vertex buffer is retained.
The spare pool is capped at 65,536 packets. That is not a total live packet cap:
same-frame revisions can retain up to the bounded occurrence demand, and churn
above the spare cap can allocate again. Source objects, dynamic capture, state
rows and replay commands still consume CPU memory and work.
Terrain retains world-space geometry while ordinary sprite positions can
change with camera movement. New animation/alpha/hue variants consume cache
entries until eviction. The bounded high-water cache does not shrink on camera
movement. Geometry storage is at most 16 MiB after power-of-two growth; CPU
records, dictionary keys and ordered occurrence arrays also consume memory.
Atlas/texture bindings are resolved each frame; geometry residency does not
claim atlas lifetime or device-loss recovery.

`rd_referenced_quads_per_frame`, `rd_dynamic_capture_bytes_per_frame`,
`rd_replayed_quads_per_frame` and `rd_source_packet_allocations_per_frame`
separate reference capture, dynamic vertex copies, fallback replay and packet
allocation. They do not count all managed allocations or retained capacity.
PerfDump additionally records active/fallback frames during its counter window
and whether the last frame used the pass, so an enabled toggle alone cannot be
mistaken for successful GPU rendering.

`rd_geometry_bytes_per_frame`, `rd_order_bytes_per_frame`,
`rd_upload_calls_per_frame`, `rd_dirty_quads_per_frame`, `rd_cached_quads`,
`rd_peak_cached_quads`, `rd_buffer_growths` and `rd_cache_evictions` distinguish
data reuse from draw-call reduction. Upload counters include successful writes
before a later transaction failure. The resident synthetic controls cover
unchanged zero-upload frames, one dirty quad, order-only alpha reversal, growth,
partial-upload retry and resize with identical IDs, as well as replay parity.

The state stage stores one exact 32-byte record per ordered occurrence,
including duplicates that share a geometry slot. Six floats retain the composed
transform; a uint art selector occupies offset 12, and the second row starts at
offset 16. The shader uses absolute occurrence indexing. Brightness/radius stay
in the original global push block.

An ordered batch holds up to four original art views and one hue/light pair.
First-seen art views receive immutable slots; a fifth distinct art view or a
palette change starts the next batch. A/B/A therefore keeps three texture runs
but issues one draw, with the original triangle and alpha order intact. Every
unused descriptor repeats the first valid view. No atlas repacking, filtering,
texture sorting or nonuniform descriptor indexing is used. Four literal sampler
branches sample LOD 0, matching the existing nearest sampler's fixed LOD range.
All borrowed views and selector ranges are validated before opening a draw list.
The device must support six textures and samplers per stage and uniform set.
At shader creation, the emitted SPIR-V is checked for the state offsets/stride,
flat uint interface, direct sampler bindings and core Shader capability only;
contract failure preserves whole-frame canvas fallback.
Up to 262,144 occurrences use at most 8 MiB of GPU transform rows, plus a reusable
CPU capture list and applied-byte snapshot. This is additional memory, not a
replacement for resident geometry/order or the fallback command stream.

An exact byte comparison skips unchanged valid state uploads. Any changed
nonempty stream uploads all active rows in one call; this first version does
not calculate dirty subranges. All three streams finish uploading before a draw
list opens. State allocation/growth or failed updates invalidate applied state;
the storage set is rebuilt whenever a dependency changes. Shrink leaves an
unused tail; empty passes clear the target without storage binding or upload;
regrowth reuploads. GPU allocation/capacity failure keeps whole-frame canvas fallback.

`rd_state_bytes_per_frame`, `rd_state_upload_calls_per_frame` and
`rd_changed_state_streams_per_frame` count successful bulk writes, including
writes preceding a later failure; uploaded rows are not individually dirty rows.
`rd_state_buffer_growths` counts new state allocations. `rd_active_state_bytes`
and `rd_state_capacity_bytes` are last-reported device gauges, not a sum across
instances; failed preparation can leave an older value.
`rd_occurrences_per_frame`, `rd_texture_runs_per_frame` and `rd_texture_batches_per_frame`
count prepared streams, including attempts whose drawing later fails. Actual
issued draws remain `rd_draw_calls_per_frame`. Total uploaded bytes/calls include
state. No counter is a GPU timer or a promise of higher FPS.

The RD stage includes exact same-texture transform batching, A/B/A
overlap order, active-RD negative controls for a wrong art slot and omitting the
absolute offset in a later batch with more than four textures,
zero warm state writes, one/far/camera changes, global brightness and circle
changes, state-only capacity growth, shrink/empty/regrow, resize, failure before
and after state upload with identical retries, and canceled-frame replacement.
Texture controls also cover different image sizes and edge UVs, fifth-view and
repeated flushes, selector-only uploads, palette replacement, invalid-selector
fallback, post-record replay, resize and canceled binding replacement.

The initial PC experiment passed 54 synthetic RD checks and four static-world
scene comparisons at 2560x1440, zoom 2, with both captures on RD and zero
repeatable or stable-pixel differences. It was slower than canvas in all four
paired scenes. This establishes a tested pixel/material boundary, not full
gameplay acceptance or a performance win. Dynamic live parity, fractional-zoom
world scenes and device RD validation remain outstanding. Preserve the toggle
off; the exported terrain optimization does not depend on this experiment.

`rd_record_cpu_ms` covers the render callback, including initialization and
fallback handling, but excludes capture, queued waiting and GPU execution.
Draw counters count actual issued draw commands, including commands before a
later failure. Successful presentation is counted separately by `rd_frames_drawn`.
