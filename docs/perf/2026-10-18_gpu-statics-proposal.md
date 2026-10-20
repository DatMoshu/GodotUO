# GPU statics: the 2026-10-18 proposal, reconciled

## Status and scope

Historical design note, reconciled 2026-10-20. This branch originally called
its draft "ADR-0037: Statics on the GPU". That number belongs to water render
modes. The GPU proposal continued as the **2026-10-19 amendment to ADR-0007**;
it is not a separate ADR and this note does not accept it or amend any decision.

The [original draft at commit 376477c7](https://github.com/DatMoshu/GodotUO/blob/376477c7/docs/architecture/ADR-0037-statics-on-the-gpu.md)
is preserved in history. For the consolidated proposal, read the
[ADR-0007 amendment](https://github.com/DatMoshu/GodotUO/blob/4b312f4873e192eb40252e8f038389b2de690ef2/docs/architecture/ADR-0007-sorted-world-in-batched-meshes.md#amendment-2026-10-19-a-depth-buffer-for-the-world-pass-statics-kept-on-the-gpu).
These links pin the records reviewed during cleanup; this older branch does
not contain the amendment or the later probe. The amendment remains Proposed
in that snapshot, awaiting the owner.

Layer: L0 renderer parity/performance proposal. This cleanup changes only
documentation. It adds no toggle, profile field, Options entry, shader or
runtime behavior. There is no new scenario or doctor requirement for this note.

## Measured motivation

The [PERF-03 breakdown](https://github.com/DatMoshu/GodotUO/blob/4b312f4873e192eb40252e8f038389b2de690ef2/docs/perf/2026-10-18_britain_breakdown.md)
records the original baseline: RTX 4090, Ryzen 7 7800X3D, optimized build,
2560x1440, zoom 2.5, 360 frames per scene, pacing disabled. Other projects
contributed 22-48% CPU load between runs, so this was not a fleet-wide quiet
window.

| Scene | Mean ms | FPS | Statics drawn | Covering land drawn |
|---|---:|---:|---:|---:|
| Britain bank | 39.8 | 25.2 | 18,329 | 6,847 |
| Dense forest | 19.1 | 52.5 | 11,449 | 2,048 |
| Dungeon | 23.9 | 41.8 | 2,282 | 6,116 |
| Cave roof | 4.5 | 223.7 | 1,036 | 6 |
| Open sea | 9.6 | 104.6 | 2,638 | 1,070 |

Those are pre-PERF-REG measurements, not an implementation result. The
[later baseline and consolidated design](https://github.com/DatMoshu/GodotUO/blob/4b312f4873e192eb40252e8f038389b2de690ef2/docs/architecture/ADR-0007-sorted-world-in-batched-meshes.md#why-now)
include the Windows land-array and merged-cover defaults: the bank measures
25.3 ms / 39.5 FPS. Compare future GPU work against that baseline under matched
conditions. The community's reported city at zoom 1.0 and GUO's bank at zoom
2.5 are different workloads; matching FPS alone does not reproduce the report.
The reported slow cave was also not reproduced by the cave-roof scene.

## Proposal carried forward

The proposed world pass uses a depth-tested 3D viewport, with cached static
instances per chunk and atlas page. Dynamic sprites use the same world depth
buffer; the canvas remains the fallback. Static buffers rebuild when chunks
change rather than once per frame. The existing light and UI passes remain
outside that world pass. The consolidated switch name is `--gpu-world`, off
by default; the earlier draft's `--gpu-statics` name is obsolete.

ADR-0001 still governs the presenter seam and shared `CalculateDepthZ()` for
drawing and picking. Removing instanced statics from the per-frame queue and
replacing queue order with a depth test require the explicit ADR-0001 amendment
identified in the consolidated proposal. No second presenter is authorized.
Nearest-neighbour sampling remains mandatory.

## Evidence limits and implementation gates

The [step-1 depth probe report](https://github.com/DatMoshu/GodotUO/blob/4b312f4873e192eb40252e8f038389b2de690ef2/docs/perf/2026-10-19_depth_probe.md)
reports 39/39 synthetic checks passing on Forward+ and 4/39 on Compatibility.
It explicitly says that no client world objects use the pass yet. This proves
neither live-world parity nor a GPU-statics performance gain. Compatibility
color conversion and depth precision remain unresolved in that report.

Before implementing the remaining proposal, resolve these draft assumptions:

- **Depth and ties:** a 28-bit packed integer is not generally exact in a
  32-bit float, which has 24 significant binary digits. Test distinct keys
  throughout the range, ties across atlas pages, and both submission orders.
  A 4-bit tile index must not silently overflow on tiles with more than 16
  entries. Submission order within one page cannot establish order across
  pages. Retain the canvas fallback when the ordering cannot be represented.
- **Blending:** per-page grouping must preserve the global back-to-front
  order of translucent and additive sprites, including overlapping pages.
  Depth testing alone does not establish blend order.
- **Instance layout:** the draft names more payload fields than it maps to
  concrete channels. Specify the transform, UV, hue, depth, object ID and
  visibility layout and verify its precision before claiming parity.
- **Changing appearance:** verify invalidation for roofs, fades, seasons,
  themes, highlights, range/death hues, lights and picking. Shader culling
  must be paired with a way to find newly fading statics without a full walk.
- **Acceptance:** require live-world pixel A/B, matching render dumps,
  eight-place upstream comparison, picking and post-processing ID checks,
  plus matched before/after timings. Keep the fallback until these pass;
  removing covering land or changing the default is a later owner decision.

The original draft's expected CPU savings and 100+ FPS target are hypotheses.
No measured "after" table for instanced statics is present in this branch.
