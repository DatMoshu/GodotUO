# ADR-0042: UO+ ambient biome particles

Status: Proposed for owner review, 2026-10-20. Implements the owner's night-push
particle brief; merging remains an owner decision.

## Context

The owner requested optional non-parity fog banks, drifting leaves and dust.
The shard supplies no biome metadata. Native land IDs and foliage flags are
available in already-visible chunks. Weather and plant sway belong to another lane.

## Decision

L1 UO+ client options, three independent toggles, all off by default. Both
Options interfaces expose fog, leaves, dust, density (0–100%) and a per-frame
particle cap (0–512; default 128). Density defaults to 50%; zero density or
zero cap disables the path. No chat command, shader, service or dependency.

One guarded draw hook immediately before native weather consumes loaded
visible tiles. It never fills or sorts RenderLists, changes picking, loads
chunks or alters native art. A tile hash selects stable emitters; time-based
phases provide motion without pools, accumulated simulation or global RNG.
At 100% density, about one in eight eligible tiles emits a particle. Motion
fades to zero at phase wrap. Camera transform and world-target clipping are
the existing batcher's. Particles receive the existing world lighting pass.

Native swamp interiors (0x3DEB–0x3DEF) emit mist; forest/jungle interiors
(0xC4–0xC7, 0xAC–0xAF) and foliage tiles emit leaves; soft sand
(0x16–0x19) emits dust. IDs come from MapGen's existing native tile tables.
Wet, unknown and covered tiles are quiet. Roof or raised surface objects on
the emitter tile suppress it even when roofs are hidden in Options. This is
a conservative local cover test, not a biome protocol or volumetric model.

Hard limits: 4096 sampled tiles/frame, 64 linked objects/tile per scan,
512 particles/frame, one batcher sprite submission per particle. The profile
cap is clamped at consumption as well as in Options. No resources are built
when disabled. The one small code-generated mist texture is lazy and released
on disabling or scene unload. Native art and mist remain nearest sampled.

## Consequences

The classic off-state takes only the gate; no tile walk or sprite submission.
The enabled path is an atmospheric overlay like weather: it does not occlude
individual pixels behind walls, trees or adjacent roofs. Mist can drift across
a neighboring tile edge. Custom ground has no inferred biome unless foliage
is present. Reaching the sample/cap limit truncates visible-chunk iteration;
it does not allocate or defer excess work. These are explicit v1 limitations.
No external packs or user-authored presets ship; expansion should use typed
Store content instead of growing the built-in set.

## Alternatives

GPU particles would add new engine lifecycle and shader behavior. Persistent
world emitters would need teleport/map cleanup and allocation budgets. Neither
is needed for this bounded first implementation. Guessing a biome from region
names would be shard-specific and would misclassify interiors.

## Validation

`client.ambient.particles` and the smoke line run `AmbientParticlesProbe`:
old-profile defaults, all-field persistence, each biome and toggle, roof/wet
suppression, density, invalid values, emission/sample limits and reproducible
time-based motion. The same scene with `-- --render-particles` and environment
`GUO_PARTICLES_PROOF` writes synthetic fog/leaves/dust frames through the real
batcher. It requires visible enabled pixels and zero baseline/disabled RGB
differences. This proves the batcher seam, not live-world visual parity.

## ADR Dependencies

ADR-0001 (sorting/picking untouched), ADR-0002 (existing batcher), ADR-0027
(default-off L1 options), ADR-0039 (existing light composition; no new light API).

## Engine Compatibility

Pinned Godot 4.7.2 mono. Reuses verified project `Image.CreateFromData`,
`ImageTexture.CreateFromImage`, batcher draw and render-target APIs. No new
engine rendering feature. Compile and rendered probe evidence accompany PR.

## GDD Requirements Addressed

Owner's 2026-10-20 night-push UO+ particle request; no registered GDD.
