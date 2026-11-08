# ADR-0049: Physical structure and native receivers for enhanced lighting

## Status

Architecture and Britain-first implementation authorized by Moshu on 2026-10-31,
following the Sosaria geometry research: "I approve your proposed sequence do
britain." Implementation and native/performance acceptance remain separate.
The existing enhanced lighting option remains opt-in; Classic is unchanged.

## Context

The native surface pass already identifies visible sprite pixels in ClassicUO
draw order. Its sun stamps lose caster/receiver height association, while the
local-light grid retains only selected tops/covers. Neither is a complete
representation of stacked floors, roof shelter or moving doors.

Sosaria's reconstruction provides useful terrain, wall and roof methods, but
its merged textured meshes are not authoritative runtime geometry. They include
inferred faces, alpha cards, separate physical/classic roof twins and incomplete
source lineage. GUO must derive structure from its own effective client data and
live world, preserving provenance and uncertainty.

## Decision

1. Keep native sprite colour, hues, nearest sampling, ordering and picking.
   Physical geometry is a parallel lighting input, never a second visible world.
2. Use canonical X/Y tiles and native UO Z. The physical metric for new sun
   geometry is `ZScale = 4 / (22 * sqrt(2))` tiles per UO Z, matching the native
   sprite projection. A unit XY shadow heading and legacy length L imply shadow
   travel `XY = heading * Z * ZScale * L`. The existing lamp falloff metric
   `Z / 11` is retained until its separate calibration; no preset migration is
   hidden in the structural sun change.
3. Prepare a bounded physical snapshot from effective map/static records plus
   current item/multi state. Keep physical roof/floor/wall membership independent
   of draw visibility and cutaway. Use stable source/owner identities, layers,
   explicit completeness, bounds and immutable frame ownership.
4. Begin with qualified structural terrain, floors, walls and roofs. Unsupported
   blockers/openings remain explicit unknowns; decorative exclusions are
   distinguished from uncertified structure. Never interpret overflow or an
   incomplete page/ray as known clear space.
5. Render qualified structure into a bounded light-space depth map, sampled by
   physical receiver XYZ from the native surface stream. Preserve the existing
   frame admission, render-thread ownership and classic failure fallback.
   First prove central Britain; report unsupported domains and low-angle bounds.
6. Add dynamic local visibility and weather shelter using that same structure,
   then measured AO/sky/terrain visibility caches. Moving or removed structures
   must restore light; a dark baked result cannot be repaired only by adding
   new blockers. Optional bakes are versioned, data-only local cache/pack content.

## Receiver correspondence and limits

Native alpha and actual sampled art/frame select visibility. Analytic surfaces
or qualified sidecars supply the physical point and normal. A sprite's apparent
roof height is not automatically the physical roof plane. Do not load both
Sosaria roof twins as blockers or apply one global roof offset. Layer ownership
and positive/negative elevated receiver cases are required before acceptance.

Sun completeness includes the full receiver-to-source corridor, including
off-screen structures. Observed local maximum Z is not a global caster bound.
An opaque hit from qualified geometry proves blockage even when the distant
corridor is incomplete. A miss is certified visible only with full source
coverage; otherwise it remains an explicitly unknown fallback. Unknown geometry
never supplies an invented blocker or a claim of empty sky.
The first bounded implementation may report reduced coverage; it must not report
that as full-world correctness.

## Alternatives

- Per-pixel CPU reference queries: retain as small-fixture oracles, not a shipping
  render path. Their bounded flat-layer domain is narrower than the world.
- Full textured 3D viewer import: unnecessary art/atlas memory, wrong visibility
  ownership and stale data lineage. Reuse pre-merge geometry methods instead.
- Whole-world final lightmaps: poor fit for time, weather, doors and mutable
  houses. Prefer stable geometry and selective measured visibility products.
- Dense voxels or built-in GI: deferred until direct geometry/receiver correctness
  and a measured cost comparison justify them.

## Engine and layers

Depends on ADR-0001 native presentation, ADR-0044 ordered RenderingDevice world
and ADR-0048 surface/light transactions. Uses the pinned Godot .NET
RenderingDevice API already exercised by these passes. Compatibility and failed
RD transactions retain Classic. No runtime Python, external service or model.
Offline authoring belongs in Tools/L3, optional data in L2, and the runtime
extension stays behind the existing enhanced option while being qualified.

The first local-light implementation augments one existing selected lamp with
six bounded depth faces; it does not increase the selected lamp budget. Overhead
weather shelter shares the physical snapshot and actual receiver surface.
Unknown classifications preserve the existing fallback. Final presentation
restores the prior realtime renderer's max-channel light normalization after
softening, retaining unmodified HDR diagnostics and native pixel sampling.

Before local sky visibility can attenuate ambient light, separate compatibility
sky/direct terms without changing the current output. Existing sun masks already
encode artistic strength, including R8 quantization; the direct budget rounds
that strength upward to an R8 step. Preserve the original neutral expression
and isolate diagnostic term selection to Debug fixtures. This split retains
the current night-floor and roof policy and does not establish physical
irradiance or authorize a new brightness policy. A future visibility cache
still needs a bounded supported domain and explicit unknown fallback.

## Acceptance

- Native Britain structural shadow positive, ground-caster/upper-floor negative
  and tall-caster/roof positive; source-isolated captures, no doubled shadows.
- Physical shelter unchanged by roof cutaway; first coherent published frame
  handles roof/door add, removal and movement without a stale mask.
- Closed/open door and low-first/high-second obstacle; beams permit light below.
- Actual native receiver metadata, exact off/failure parity and complete resource
  retirement on disable, resize, scene change and shutdown.
- Bounded CPU/GPU residency, geometry production cost, cold/warm timing and
  supported-domain counters. Release/shared-load evidence is labelled honestly.
- Appearance review of the bright plaza, awning shadows, upper surfaces, player
  readability and facade after individual light sources have been isolated.

No full-world, all-platform, physically exact proxy or full-GI claim is made by
this ADR or by passing a synthetic helper test.
