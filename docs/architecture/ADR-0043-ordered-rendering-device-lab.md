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
