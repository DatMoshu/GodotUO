# ADR: UO+ weather in Labs

## Status

Owner-requested scope (2026-10-20 night brief); implementation proposed for PR
review. No approval to promote to L1 or merge is implied.

## Decision

Classify weather additions as L4: default-off, session-local experiments compiled
only with `GuoLabs=true` in Debug. Each effect has an independent checkbox in a
panel registered through the shared Labs command/Options hook. No new profile
fields, downloaded assets, dependencies, shaders or external processes.

Surface effects enter the existing RenderLists queue using GameObject's shared
CalculateDepthZ. Player drops use the union of current animation alpha masks and
draw after equipment inside its sorted draw call. Sampling remains nearest.
All ported-file seams are marked `PORT DEVIATION (GUO)` and compile away outside
Labs. The standard weather renderer is untouched.

## Context and alternatives

The owner requested water rain rings, filling/draining cobblestone and listed-tile
puddles, and a fading wet-player outline. Directly extending the water shader
would couple this lane to simultaneous water work and omit Classic water.
A fullscreen overlay would paint rings across foreground objects. Independent
pixel primitives in the existing painter queue avoid both problems.

## ADR dependencies and engine compatibility

Depends on ADR-0001/0002 (scene seam and canvas batcher) and the existing L4 build
policy. Uses existing UltimaBatcher2D primitives and Animations.PixelCheck;
Godot 4.7.2 mono compilation verifies the Window/controls APIs. No new engine API
is required for the effects. The synthetic visual probe verifies primitive
rendering; live-world visual acceptance remains a separate scenario step.

## Consequences and verification

The cache and per-frame budgets bound work. Puddles can be evicted by travel or
cache collision; this is visual state only. Tile IDs are chosen locally and
validated without shipping proprietary tile lists. Settings deliberately reset
when a World is replaced. Sitting silhouettes remain future
work. No gameplay, network weather or picking semantics change.

Run WeatherProbe for defaults, independent toggles, fill/drain, fade, ring expiry,
delta caps, budget clamps and tile validation. Default/off parity is structural:
default builds exclude Labs and the guarded seams. Live off/on screenshots and
roof/occlusion checks are defined in the scenario. Promotion to L1 requires
profile persistence, the L1 documentation contract and measured off-state parity.
