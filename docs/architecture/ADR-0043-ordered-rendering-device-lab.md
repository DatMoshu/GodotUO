# ADR-0043: ordered RenderingDevice world prototype

Date: 2026-10-23. Status: owner-authorized L4 experiment, off by default.

The owner requested a new GPU renderer prototype after the lighting and
terrain fixes, with 1:1 original art and comparisons against ClassicUO.
This authorizes an experiment, not a change to the default renderer or a
relaxation of pixel parity. It does not replace gameplay, networking, asset
readers, picking, or the existing export renderer.

Use stock Godot 4.7.2 RenderingDevice from C#. Submit the existing complete
ordered world stream to a byte-color target, including base land, loose land,
sorted objects/cover insertions and transparent objects. Expand quad records
and apply the camera transform on the GPU. Preserve nearest sampling, hue
arithmetic, triangle winding and submission order. Do not depend on the old
GPU-world depth key or Godot's automatic 3D transparency ordering.

The target starts opaque black, as the current world target does. Rendering
only transparent foreground to an intermediate and then blending it over
terrain regroups integer blend operations and can change pixels. Existing
classic lighting, selection/weather and UI compositing remain downstream.

The first experiment measures bulk submission through a persistent GPU buffer;
it still gathers and sorts on the CPU. Static residency, dirty range uploads,
GPU culling and native extensions are subsequent experiments after this
material/presentation boundary is proven. No speedup is assumed.

All RD resource access runs on Godot's rendering thread, using the global RD.
Borrowed atlas RIDs remain owned by their textures. No local RD, synchronous
GPU readback or per-frame resource creation belongs in the live draw path.
Unsupported renderer/features, initialization or capture failure keep the
complete canvas world. Never present a partial or previous frame as success.
Cancel an unsubmitted capture before recycling its canvas items if native
FramePreDraw was skipped; submitted callbacks retain their data until retirement.

The owner subsequently authorized removal of duplicate canvas work and resident
world data with dirty updates. Capture commands atomically and preserve a cold
canvas replay plan at the same per-quad granularity. Early rejection emits the
captured prefix, then resumes ordinary drawing; late failure emits the whole
plan. Do not repeat gameplay traversal. The initial replay path requires the
same rendering thread; separate-thread mode falls back before capture. This
does not promise recovery after native device loss or explicitly disposed art.
Keep performance stages separately measurable against the original prototype
and CUO, without changing the parity or export requirements.

The next stage separates resident quad records from the ordered occurrence
stream. Cache exact full vertex content in bounded slots, pin current hits
before eviction, coalesce dirty slot uploads and update order independently.
Never deduplicate ordered occurrences or reorder by texture. A new GPU
allocation is invalid regardless of unchanged CPU IDs; partial failures must
force a retry. This stage retains per-frame CPU capture and comparison, and
does not yet promise persistent gameplay IDs or camera-independent sprites.
Measure CPU cache cost and actual upload bytes separately from GPU draw count.

When the RD toggle is enabled, clear every canvas item used by the preceding
frame once. The unused pool tail already has no commands after its first idle
frame, so subsequent RD frames need not clear that tail again. Retain the pool
for reuse, preserve private fallback-child cleanup and cancel pending tickets
before recycling items. Ordinary toggle-off and non-Labs clearing stay unchanged.

Each ordered occurrence also carries the exact already-composed transform and
an art selector in a 32-byte record, separate from resident geometry and slot IDs.
The six transform floats keep offsets 0/4/8 and 16/20/24; a uint selector uses
offset 12. The vertex shader indexes
state by absolute ordered occurrence, not resident slot or draw-local index.
Brightness, circle radius and target dimensions retain the existing push block.
Keep at most 262,144 occurrences per captured frame; overflow uses canvas.

The first state-buffer implementation compares exact active bytes with an
applied snapshot. Unchanged valid frames upload nothing; a changed nonempty
stream uploads all active rows once before DrawListBegin. New allocations and
uncertain uploads invalidate applied state. Empty passes clear the target with
zero draws, and regrowth uploads the new active stream. Preserve the frozen
ticket and complete canvas replay independently of all three GPU buffers.
Measure added capture/comparison and bandwidth costs against actual texture
runs, issued draws and matched wall times; fewer draws alone are not acceptance.

Permit an ordered draw to bind up to four original art views with one hue/light
pair. Preserve the original triangle stream: assign slots in first-seen order,
never reuse a slot within that batch, and start a new batch for a fifth distinct
view or a palette change. Cache by the ordered complete binding tuple, retaining
all original texture owners. Use four literal sampler bindings and explicit
LOD 0 selection with the existing nearest/clamp/fixed-LOD sampler. Require six
sampled-image and sampler descriptors per stage and set; no optional nonuniform
indexing feature is assumed. Validate emitted state layout, flat uint selector,
direct sampler bindings and capabilities, as well as every input selector and
borrowed view. Keep the original complete canvas replay on any failure.

The motivating four-scene diagnostic found four original art views per frame,
constant hue/light and no RS-to-RD aliases. Greedy four/eight/sixteen-slot dry
counts all gave one batch. This justifies testing four slots; it is neither a
draw measurement nor a speed prediction. Test later-batch absolute addressing,
wrong-slot negative controls, edge UVs, overlapping alpha and resource lifetime
before comparing actual draws and matched CPU/wall measurements against CUO.

The implementation belongs to `src/Labs/GpuRenderer`, compiled only with
`GuoLabs=true` in Debug. The shared Labs command/options registry supplies
the runtime toggle. Compatibility and exported clients retain the existing
renderer. Promotion requires a separate decision.

Acceptance: synthetic material/full-presentation pixel comparisons; zero
unexpected differences for deterministic scene inputs; resize/unload and
unsupported-command fallback checks; explicit active/fallback counters;
repeated matching CPU metrics against CUO plus GUO wall-frame/allocation
measurements. A prototype that fails parity remains opt-in and is reported as
an experiment, even if faster. Do not claim a CUO FPS ratio from unlike clocks.
