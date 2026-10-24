# Atlas uploads v2 (PERF-LANE-03, review item G)

## Problem

Godot cannot upload part of a texture: `ImageTexture.Update` replaces the
whole page. `TextureAtlas` therefore re-sends every page a new sprite landed
on, once per frame, in full. While walking, new art, animation frames, gumps
and glyphs land on the active pages almost every step; the minimap repainted a
region of the gump atlas on every step and so re-sent a whole gump page too.
The lane's runtime attribution counted 147 full 2048x2048 page uploads in a 60 s
walk (about 1.9 ms each), 380 ms of summed upload time, with 41 ms frames where
several uploads met and an 81 ms worst frame.

## Change (off by default: `GUO_ATLAS_V2=1`)

Layer: L0 renderer seam, `PORT DEVIATION (GUO)`, behind an environment toggle
for the owner to test; no profile field or Options entry until that decision.

1. **Smaller dynamic pages.** The art, animation, gump and glyph atlases use
   1024x1024 pages (4 MiB per upload instead of 16 MiB). Land, texmaps and
   lights keep 2048. Packing is upstream's; a sprite lands on a different page
   and position, every UV is still an exact texel and sampling stays nearest.
   `LandPages` already copies a smaller page into a layer's corner with an
   exact scale. A sprite larger than a page keeps its dedicated texture.
2. **Upload budget.** In the world, at most two dirty pages are uploaded per
   frame, the page that became dirty first going first; the rest wait for the
   next frames. A page dirtied every frame cannot starve the others.
3. **Pre-warm.** Before the world is entered, and for one second after, the
   budget does not apply: everything decoded at login and for the first view
   is uploaded at once, as today.
4. **Minimap.** The minimap paints into a texture of its own (a few hundred
   pixels square) instead of a region of the gump atlas, so a step uploads
   that, not a page.

## The product decision (director's #14209)

With the budget, a sprite decoded this frame on a page that has to wait is
drawn from the page's previous contents (a transparent region) until its page
is uploaded: **it can appear one or more frames later than today**. At two
uploads per frame and 1024 pages that is normally one frame; a teleport that
dirties many pages spreads them over a few frames instead of one long frame.
Today every new sprite already appears one frame late (uploads happen at the
start of the next frame), so the change is in how many frames, not whether.
The minimap now shows a step in the same frame instead of the next one. This
is why the toggle is off by default: the owner decides whether the trade is
acceptable after seeing it.

## Checks

The batcher probe checks the page cap per owner with the toggle off and on,
that the budget uploads the two oldest dirty pages and leaves the third dirty,
and that a waiting page goes before a newly dirtied one. The probe report
records `atlas_v2`, `atlas_page_uploads` and `atlas_deferred_page_frames`.

## Measurement

Pending a runtime window: frozen-world parity (login, bank, forest, dungeon,
docks) off/on beside an off/off control pair taken in the same window, and the
pinned 46-tile bank-to-docks walk off/on, p50/p95/p99 and upload counts, with
the sample window's host load. Results will be added here.
