# ADR-0045: Bounded local overview beyond 5x

## Status

Accepted by Moshu, 2026-10-27, in the GUO performance reviewer conversation:
"make zoom changes and then launch it for me to test" following the proposal
for a compact cache, reduced detail above 5x and bounded preparation.

## Decision

The existing explicit `--zoom-max` extension enters a view-only isometric map
overview above 5x. At 5x and below the existing world gather/presenter/picking
remains unchanged. This extends the existing opt-in zoom path, introduces no
profile option and changes no default camera range. The save/load clamp to 2.5
remains. This is a scoped exception to ADR-0001's full-detail parity requirement
at extreme zoom, not an additional general scene presenter.

Overview reads the user's local map blocks directly, without creating distant
Land/Static objects, meshes, art atlas entries or mobile draw queues. Radar
colours show terrain and structural footprints (walls, roofs, surfaces and
features at least 16 Z units high). Small props, mobiles, labels, effects and
dynamic item/multi art are omitted. A fixed-size player marker remains.
This is a flat regional overview, not the full elevated/sprite view. It does
not change networking, simulation, collision, server interest range or macros.
World mouse picking/drag/double-click actions are suppressed; right release
still stops a movement begun before the transition. Zoom and ordinary UI work.

256x256-tile textures use nearest sampling and no mipmaps. A scene owns up to
128 entries: at most 32 MiB CPU RGBA pixels and 32 MiB texture payload, plus
small metadata and temporary upload storage. Visible hits are pinned before
LRU eviction; nearest misses prepare first. Each frame reads at most 128 map
blocks, modifies at most two tiles and checks a 4 ms deadline between blocks.
The deadline cannot preempt a synchronous file read. Missing regions fill in
progressively; they do not block the input loop until the whole region loads.
If an unusually large view exceeds the tile budget, the nearest 128 tiles win.
No asynchronous jobs outlive the view. Returning to a warm view reuses pixels
and textures. Cache lifetime ends at scene unload; facet, map-loader identity,
map-data revision, season and theme revision invalidate it. Map patches and
UltimaLive map/static writes advance the map revision.

The overview uses the existing batcher canvas seam on both RenderingDevice and
Compatibility; it skips the detailed ordered stream and lights. Returning to
normal zoom refreshes positions and roof cutaway state. The existing per-frame
canvas lifecycle clears prior RD presentation. Existing ordinary render
invalidation/order/duplicates are unchanged.

## Verification

`tools/scenarios/client/wide_zoom_overview.cs.fixture` tests hard work/cache
bounds, warm recall, eviction/cancellation, filtering, actual offline world
transitions through 2.5/5/5.1/10/20 and back, map invalidation, mouse picking and
the absence of distant rich-chunk loads. Run on RD and Compatibility in an
external diagnostic stage; remove injection before full smoke and export.
Screenshots establish the overview presentation. No benchmark or FPS claim.
Godot calls reuse existing ImageTexture/batcher patterns and are compiled and
exercised on the pinned engine. See data_formats, Wide-zoom overview.
