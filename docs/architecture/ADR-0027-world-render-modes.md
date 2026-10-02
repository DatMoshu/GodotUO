# ADR-0027: World render modes and map layers in the editor

## Status

Accepted — 2026-10-02 (the owner approved the plan).

## Date

2026-10-02

## Decision Makers

The owner, relayed by the director (GUO-Director).

## Summary

The editor's UO World tab and its radar get **render modes** (diagnostic
views that recolour the world: height, walkability, reachability, types,
IDs, land mesh, problems, project diff) and **map layers** in the manner of
an online map (places, regions, spawns, houses, live players, pins, measure,
route, minimap). A **scene pack** export writes a frame, the chosen mode
images and a pixel-to-cell map, for review by a vision model.

## Context

The World tab draws with the game's own renderer (ADR-0015). What the world
*means* (where a player can walk, where a region ends, what a spawner covers,
which blocks a project changed) is invisible in it. Builders checked these by
walking a character, and reviews of new buildings needed separate offline
renders (height, walkability, category colours, holes, wall links) made by a
tool outside this repository. Those renders proved useful and are the model
for these modes.

## Decision

1. **Modes are editor overlays, never renderer changes** (`docs/editor_plan.md`
   §4.4). Each mode is a class in `addons/guo_editor/World/Modes/`
   implementing one small interface (draw the visible cells, give a legend).
   One `Node2D` drawn after the world canvas and the guides hosts the active
   mode. Cell geometry is shared with `WorldGuides` (one helper).
2. **Walkability uses the client's own movement rules** (the pathfinder's
   checks over tiledata flags and heights), so the mode shows what the client
   would let a player do. A shard can be stricter; the Live layer shows what
   the shard says when connected.
3. **Layers read data the editor already has** or that a shard folder
   provides: world project blocks, world objects (ADR-0014), pack regions
   (ADR-0026), ModernUO `Data/regions.json` and `Data/Locations` when a shard
   folder is set, the Shard dock's bridge for live objects. Pins live in the
   world project. Nothing is written to the install.
4. **The radar and the World tab share layers**: the same layer list draws on
   both, the radar at its own scale.
5. **Scene packs** go to `build/scene_packs/<stamp>/`: `shot.png`, one image
   per mode chosen, and `scene.json` (camera, pixel-to-cell mapping, objects
   with screen boxes). Sending one to a vision model is a separate action
   that uses the user's own tool and key; GUO stores no AI keys.

## Consequences

- Overlays redraw every frame over the visible cells only; heavy modes
  (reachability, problems) compute on demand and cache per block.
- Frames of these modes show client art and stay under `build/`.
- The editor smoke gets one check per mode at a known spot.

## Validation

To do: per-mode smoke checks; a tour segment per mode; a scene pack reviewed
by a vision model, results shown to the owner before anything is shared.
