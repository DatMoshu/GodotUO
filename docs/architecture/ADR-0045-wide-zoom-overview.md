# ADR-0045: Bounded local overview and whole-facet zoom

## Status

Accepted by Moshu, 2026-10-27, in the GUO performance reviewer conversation.
Initial request: "make zoom changes and then launch it for me to test".
Revision: "Make it do this at 15x and can we then zoom out to show the entire world?"
The revision supersedes the initial above-5x transition.

## Decision

An explicit `--zoom-max` of at least 15 enables the overview extension. It
switches at exactly 15x; below 15x the existing native world gather/presenter
and picking remain. Default camera ranges remain unchanged. Save/load still
clamps above 2.5. This is the owner-approved exception to ADR-0001 at extreme
zoom, not a general alternate presenter.

In this extension the upper camera bound is calculated from the active facet's
dimensions and current viewport, fitting its entire isometric diamond with a
32-pixel margin. It replaces the numeric extended ceiling. Resizes/facet changes
recalculate it. Above 15 the Ctrl+wheel step multiplies/divides zoom by 1.2;
below it the ordinary step remains, with a snap to the 15 boundary. The view
smoothly recentres from the player to the facet centre as zoom approaches the
fit bound. At the bound all corners are visible, even from an edge player
position. Whole world means the current facet, not simultaneous separate facets.

Overview reads local map data without creating distant Land/Static objects,
meshes, art atlases or mobile queues. Radar colours represent terrain and
structural footprints (walls, roofs, surfaces and features at least 16 Z units
high). It omits small props, mobiles, labels, effects and dynamic item/multi
art. A fixed-size player marker remains. It is flat and view-only for world
mouse actions. Simulation, networking, collision, macros and ordinary UI stay
unchanged. Right release still stops movement begun before the transition.

## Bounded multiresolution cache

Cache keys include tile coordinate and power-of-two sample level. Textures
remain 256x256 RGBA, but increasingly distant levels cover larger map regions.
Choose level from screen zoom, then coarsen further if needed so every visible
tile fits the 128-entry bound. The full facet is never truncated to the nearest
entries. Nearest sampling, no mipmaps. Coarse levels deliberately sample away
subpixel features; native art is unchanged below 15.

At most 32 MiB CPU pixels and 32 MiB texture payload plus metadata/temporary
upload storage. Visible hits pin before LRU eviction. Nearest misses prepare
first; obsolete view requests perform no further work. Existing cached levels
remain as a preview while the requested level fills. Returning to a warm level
needs no decoding. Current-level pixels draw last over that preview.

Fine-level frames read at most 128 native map blocks; coarse frames at most
512. Source-read accounting includes all sampled blocks, rather than counting
one coarse raster block as one read. At most two texture uploads per frame,
with a 4 ms soft deadline between units. A synchronous read/unit cannot be
preempted; loading remains progressive. No background jobs or disk cache.

Facet, loader identity, map-data revision, season and theme invalidate all
levels. Map patches/UltimaLive writes advance revision, including empty static
blocks. Distant UltimaLive indices refresh without requiring rich chunks.
Unload disposes the cache. The ordinary renderer's ordering/reuse is unchanged.

## Verification

`tools/scenarios/client/wide_zoom_overview.cs.fixture`: geometry checks across
facets, viewport sizes and player corners; exact threshold and wheel reach;
hard source-read/cache bounds and warm multilevel return; offline actual
native/15/20/full-facet/back transitions and invalidation on RD and Compatibility.
Inspect whole-facet and native-return screenshots. Clean diagnostic injection
before required full smoke/export. No benchmark or FPS claim. Engine APIs
reuse compiled/exercised patterns from the pinned Godot build. See data_formats.
