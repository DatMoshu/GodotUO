# Merged land (Epic B, B4 fix 1): quiet rerun

2026-09-28, RTX 4090, 2560x1440, zoom 2.5 (the camera's maximum), vsync,
the frame cap and upstream's draw pacing lifted, 360 frames per scene.
Order: plain, `--merged-land`, `--merged-land=ordered`, plain again. The
second plain run is slower because free memory fell from 18 to 13.6 GB when
another job started, so the first plain run is the reference. Idle during
all runs: another session's touch-probe client (about 2% CPU), an MGS5 lab
(1.4%) and the shared shard (0%).

| Scene | plain mean ms | by texture mean ms | ordered mean ms | plain draw calls | by texture | ordered | parity by texture (px) | parity ordered (px) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| open field | 24.8 | 20.6 | 23.4 | 1,743 | 539 | 1,340 | 268 | 0 |
| Britain bank | 62.1 | 57.4 | 63.4 | 8,025 | 4,047 | 7,698 | 1,115 | 0 |
| dense forest | 38.3 | 37.7 | 39.3 | 3,054 | 1,457 | 3,089 | 1,130 | 0 |
| dungeon | 34.4 | 29.7 | 36.0 | 6,484 | 4,192 | 5,902 | 469 | 52 |

**Reading it:**

- **By texture** (one mesh per texture): land meshes drop to 2-3 a frame,
  draw calls by 50-69%, and mean frame time by 1-17%. It breaks pixel parity
  along land tile edges, where neighbouring diamonds overlap and the new
  order puts a different tile on top. The maximum channel difference is 86.
- **Ordered** (only consecutive same-texture runs merged, the original order
  kept): parity is exact in three scenes (the dungeon's 52 pixels are
  unexplained; flickering torches are plausible). But consecutive runs rarely
  share a texture, so draw calls fall only 4-23% and frame time does not
  move.
- **Neither is a go as built.** Both the win and parity need all land in one
  mesh in its original order. That means one texture for all land: land
  atlas pages as layers of a `Texture2DArray` in the land mesh shader only
  (ADR-0007's step 1, scoped to land). Expected: land meshes at 2-3 like the
  by-texture merge, with exact parity.
- **Covering land** (the tiles drawn again in the sorted pass) is untouched
  by either: 345-3,557 draw calls a frame. That is fix 2.

At 1280x720 and zoom 0.7, the ordered mode is exact in all five scenes, and
frame time is within noise of plain (1.5-2.2 ms).

## Fix 1b: `--merged-land=array` (one land mesh over a Texture2DArray)

All visible land is one mesh, in the original per-chunk order; the land's
atlas pages are the layers of one `Texture2DArray`, sampled nearest by
`uo_hue_land_array.gdshader` (commit 11d3ec5). Same setup as above: 2560x1440
at zoom 2.5 unless marked, order plain / array / plain / array at the
default size.

The parity check now takes five frames (plain, test, plain, test, plain).
*Violations* are pixels stable across the three plain frames where the first
test frame differs; *strict* are those where both test frames agree with
each other and differ from plain, a repeatable rendering difference that
flicker or motion almost never produce.

| Scene | plain mean ms | array mean ms | plain again | plain draw calls | array | land meshes | parity violations (px) | strict (px) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| open field | 12.3 | 12.1 (-1%) | 12.7 | 1,739 | 540 | 1,200 -> 1 | 0 | 0 |
| Britain bank | 44.3 | 38.6 (-13%) | 43.7 | 8,000 | 4,031 | 3,976 -> 1 | 0 | 0 |
| dense forest | 24.8 | 22.1 (-11%) | 25.4 | 3,636 | 1,362 | 2,275 -> 1 | 0 | 0 |
| dungeon | 20.8 | 17.5 (-16%) | 22.0 | 6,304 | 4,192 | 2,113 -> 1 | 0 | 0 |

34-35 GB free throughout. Idle during the runs: another project's headless
Godot check (about 700 MB). The machine was quieter than for the runs
above (plain Britain bank 44 ms against 62 ms), so compare within a table,
not across them; the two plain runs bracketing the array run agree within
1-6%.

**Reading it:**

- **The array keeps what the by-texture merge won and fixes its parity.**
  Land is one mesh a frame. Draw calls fall by the same 34-69% as the
  by-texture merge (540 / 4,031 / 1,362 / 4,192 against its 539 / 4,047 /
  1,457 / 4,192), mean frame time by 1-16%, p99 by 15-24% in the three busy
  scenes, and not one stable pixel differs from plain at 2560x1440.
- **At the default size** (1280x720), parity is exact in four scenes. The
  dense forest shows 87 differing pixels, 20 of them repeatable, all on the
  overhead name of a wandering NPC, not on land.
- **The open field barely moves** (-1%): with land already cheap there, its
  frame is world prepare (6.5-7.5 ms), not drawing.
- **What remains is covering land**: 345 / 3,266 / 564 / 3,557 draw calls a
  frame, now most of what is left in Britain bank (81%) and the dungeon
  (85%). That is fix 2.
- **Recommendation:** make `--merged-land=array` the default once it has run
  on the other targets (Android, the Deck, web), where the `Texture2DArray` is the thing to
  watch: one 2048x2048 layer (16 MiB) per land atlas page, however small
  the page. Until then
  it stays behind the flag.

**The dungeon's 52 pixels** from the ordered run did not appear under the
array, in either the loose count or the strict one. Whatever they were
(flickering light is the likely cause), they were not a repeatable rendering
difference.

## Fix 2 (plan): covering land

Covering land is land drawn a second time inside the sorted pass
(`RenderLists.CoverFromBelow`, ADR-0004's amendment), one
`CanvasItemAddMesh` per tile (`UltimaBatcher2D.DrawMeshSprite`). Godot's
canvas never batches meshes, so every tile is a draw call: 345 / 3,266 /
564 / 3,557 a frame in the four scenes, 81-85% of what is left in Britain
bank and the dungeon after fix 1b. Two things set that count, and each has
its own fix:

1. **How many tiles are queued.** For each object below the land of its own
   tile, `CoverFromBelow` queues the whole square of tiles in front of it,
   `(reach + 1)^2`, up to 81. A tile at `(dx, dy)` sits `(dx - dy) * 22` px
   across from the object, so only the tiles near the diagonal can overlap
   its sprite at all.
2. **How they are submitted.** Covering tiles sit in the sorted list at their
   own depth, between statics, so only consecutive ones can share a mesh
   without changing the painter's order.

Steps, each behind a flag and gated on exact parity (five-frame check,
strict count 0 at 2560x1440 and the default size):

- **2a. Measure first (a counter, no behaviour change).** Covering runs per
  frame (consecutive `DrawMeshSprite` calls with nothing between them) and
  mean run length; and, per queued tile, whether its screen rect overlaps the
  queuing object's sprite bounds. These decide how much 2b and 2c can win.
- **2b. Queue only tiles that overlap the object** (`--cover-cull`). Redrawing
  a tile repaints the bake's own pixels, so it changes the frame only where
  something drawn earlier in the sorted pass overlaps it. The object it is
  queued for is that something; a tile outside the object's sprite rect
  (plus the diamond's stretch) repaints the bake's pixels unless some other
  earlier sprite overlaps it there. Another below-ground object queues the
  tiles it needs itself; an object above ground that the redraw happens to
  cover today is the case the cull could change, and the parity gate is what
  shows whether that ever occurs. Pure CPU culling, and it also
  cuts world-prepare time; expected to remove most of the square outside
  `|dx - dy| <= 1-2`.
- **2c. Merge each run into one mesh over the land array** (`--merged-cover`):
  the tiles of a run share `LandPages` and `uo_hue_land_array.gdshader` from
  fix 1b, cached by the run's (layer, index) sequence so a still frame
  rebuilds nothing. Draw calls fall to the number of runs; worth building only
  if 2a shows runs longer than about 3.
- **Not planned:** reordering covering tiles past sprites they do not overlap
  (merging across runs). That is ADR-0007's general sorted batching, not a
  land fix.

Order: 2a, then 2b (likely the larger and simpler win), then 2c if the
remaining runs justify it. The ClassicUO world-prepare comparison runs in the
next heavy slot after the device jobs, so it can also weigh 2b's saving
against upstream's prepare time.
