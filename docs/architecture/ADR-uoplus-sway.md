# ADR: UO+ static vegetation sway

## Status

Proposed implementation of Moshu's 2026-10-22 sprint brief, SWAY-01–03.
No merge authorization is implied.

## Decision

L1, off by default, using the existing Sway flag and individual UO+ density.
Only map statics classified by the versioned item-range data file participate.
Unknown IDs, stumps, wet tiles, walls, roofs, signs and characters remain still.
Trees, bushes, plants and grass bend above a fixed bottom; hanging ornaments fix
their top. Native pixels retain nearest sampling. The shader displaces vertices
in pixel units, with shared wind/gusts and a stable phase derived from world X/Y.
Density scales amplitude; upstream rain Count/70 raises wind strength.

Cached sprite meshes carry bend weights and phase through CUSTOM0. Both cached
chunk quads and individually drawn static art use the same path. Hue/alpha remain
in the existing packed colour, painter order is preserved, and selection continues
to use the original silhouette and CalculateDepthZ. Shadows keep their original
silhouette. Off and zero density submit the original commands, without sway meshes.
No external dependency, executable pack, service, or AI is introduced.

## Context and alternatives

The shared panel already reserves Sway. CPU deformation would repeat trigonometry
for every static every frame. Per-static shader materials would split batches and
duplicate shared hue state. One shared material with cached weighted meshes keeps
animation on the GPU and bounds the geometry cache. Unused meshes are evicted at a
frame boundary after prior canvas commands have been cleared. At the hard limit,
uncached sprites remain still.

## ADR Dependencies

ADR-0001/0002/0004: existing scene seam, canvas batcher and CUSTOM0 meshes.
ADR-uoplus-settings: flags, density, Defaults and persistence. No new registry
ownership or interface shared with other systems is introduced.

## Engine Compatibility

Godot 4.7.2 mono, canvas-item shaders, ArrayMesh RGBAFloat CUSTOM0 and
CanvasItemAddMesh, following the existing MeshLayer path. Engine reference:
docs/engine-reference/godot/VERSION.md. Shader displacement, nearest sampling,
packed hue and fixed anchors require rendered verification on the pinned build.

## Validation

The sway contract checks data classification, safe default, flags/persistence,
anchors, density and rain scaling. Runtime acceptance requires ten-second on/off
clips in Yew and Britain gardens, plus a measured frame-cost line (target <0.5 ms
at the editor view). No performance or visual claim follows from compilation.

## GDD Requirements Addressed

Moshu's sprint request: “UO+ ... tree and plant sway”. No registered GDD.
