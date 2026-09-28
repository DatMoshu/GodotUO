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

## Fix 2a and 2b measured (2026-09-28, second slot)

Same setup: 2560x1440, zoom 2.5, order plain / `--cover-cull` / plain
again / cull at the default size, 33-35 GB free.

**2a, the counters (plain run):**

| Scene | covering meshes | covering runs | mean run | queued | of which overlap their object |
|---|---:|---:|---:|---:|---:|
| open field | 345 | 173 | 2.0 | 345 | 278 (81%) |
| Britain bank | 3,266 | 796 | 4.1 | 3,291 | 2,582 (78%) |
| dense forest | 564 | 161 | 3.5 | 564 | 564 (100%) |
| dungeon | 3,557 | 629 | 5.7 | 3,557 | 2,234 (63%) |

**2b, `--cover-cull`: a no-go.** Covering meshes fall 345 -> 284, 3,266 ->
2,942 and 3,557 -> 2,817 (the forest keeps all 564, every one of them
overlapping), and mean frame time 5-10% against the mean of the two plain
runs (dungeon 20.4 -> 18.4 ms, Britain bank 44.8 -> 42.2), but pixel
parity breaks: 402, 607 and 666 stable pixels differ repeatably in the open
field, Britain bank and dungeon at 2560x1440 (185 in the dungeon at the
default size). The differences are small spots spread over the floor at tile
spacing, not around mobiles: the likely cause is the one that broke the
by-texture land merge. Neighbouring diamonds overlap along their edges, and
once a tile is redrawn but its culled neighbour is not, the redrawn tile's
edge lands over the neighbour's. The cull could only be exact by keeping
whole neighbourhoods, which gives back most of what it saves. The flag stays
in, off, as the measured record; it should not be used.

**So 2c is next, and 2a says it is worth it.** Runs average 4.1 tiles in
Britain bank and 5.7 in the dungeon, over the "about 3" the plan set: one
mesh per run would take covering land from 3,266 to about 796 draw calls in
the bank and from 3,557 to about 629 in the dungeon, and it keeps the
painter's order, so parity holds by construction rather than by luck.

**The ClassicUO comparison did not run in that slot.** Neither client
started its measurement: ClassicUO reached the world but PerfDump waited for
a first message that a quiet shard never sends, and GUO's `--play` stayed at
the login gump. Both were fixed for the third slot, below.

## Fix 2c and the ClassicUO comparison (2026-09-28, third slot)

2560x1440, zoom 2.5, `--merged-land=array` throughout, 360 frames per scene
after 180 to settle; the "-off" rows are the same run with `--merged-cover`
switched off, so they share the session's thermal and cache state.

**2c, `--merged-cover`: parity holds, draw calls fall 29-66%.** Each run of
covering-land tiles is one mesh over LandPages' Texture2DArray, cached from
frame to frame (CoverRuns). The five-frame parity check is 0 px in every
scene, and the "strict max delta" column is 0 too.

| Scene | draw calls, off -> 2c | covering meshes | mean ms, off -> 2c | p95 ms, off -> 2c |
|---|---:|---:|---:|---:|
| open field | 539 -> 379 (-30%) | 345 -> 173 | 10.7 -> 10.6 (-1%) | 13.6 -> 12.3 |
| Britain bank | 4,034 -> 1,624 (-60%) | 3,266 -> 796 | 36.7 -> 34.4 (-6%) | 41.5 -> 38.9 |
| dense forest | 803 -> 400 (-50%) | 564 -> 161 | 20.5 -> 20.3 (-1%) | 23.8 -> 23.4 |
| dungeon | 4,389 -> 1,480 (-66%) | 3,557 -> 629 | 16.2 -> 14.5 (-11%) | 21.2 -> 17.6 |

Batcher items are unchanged (1,613 in the bank either way), and world draw
ms rises by 0.1-0.6 ms, the cost of hashing and comparing the runs; the GPU
side more than repays it where covering land is dense. Against the separate
array-only run of the same session (`c2_array_far`), the mean falls 6-18%,
but that pair is further apart in time and the in-run pair above is the
number to quote.

The first build of 2c broke parity badly (10-60k repeatable px, deltas near
255: roof tiles missing over house interiors). The held run is drawn from
inside the next draw's flush, after that draw had already checked its item's
material and offset (`AddSprite`'s EnsureMaterial comes before FlushRun), so
the draw landed in whatever item FlushCover left behind: first the land-array
item, then, after a first attempt at a fix, the mesh item at the covering
offset. FlushCover now puts the item material, item offset and the pending
material and offset back exactly as it found them (it also runs inside Cut).

**Recommendation:** 2c can go on with 1b on the desktop. It needs a Thor pass
first, like 1b (the dungeon's 36 px there is still being explained).

**ClassicUO against GUO, same spot, same window and zoom.** *Caveat, found
after the run: GUO was measured as the editor runs it, the Debug
configuration, which builds with `Optimize=false`; ClassicUO was built
`-c Release`. The absolute gap below is therefore overstated, most of all
for prepare, which is plain C#. Every on/off comparison in this document
is within one build and stands. Exported builds (ExportRelease) are
optimised, so players never ran this code unoptimised.* From each
client's own profiler (the average over its last 60 frames), PerfDump in both,
ClassicUO built out of tree with it injected. GUO on the default path
(no merged flags).

| Scene | world prepare ms, CUO / GUO | world draw ms, CUO / GUO | render frame ms, CUO / GUO |
|---|---:|---:|---:|
| open field | 2.43 / 6.78 (2.8x) | 3.40 / 3.34 (1.0x) | 6.2 / 10.8 |
| Britain bank | 3.95 / 17.10 (4.3x) | 4.66 / 17.40 (3.7x) | 8.9 / 35.5 |
| dense forest | 2.85 / 10.98 (3.9x) | 4.57 / 8.98 (2.0x) | 7.7 / 20.7 |
| dungeon | 1.76 / 7.90 (4.5x) | 2.84 / 5.28 (1.9x) | 4.9 / 14.0 |

World prepare, which is ported code (the render lists), not the renderer, is
now the larger gap: 2.8-4.5x ClassicUO's in every scene, where world draw is
at parity in the open field and 1.9-3.7x elsewhere. The next B4 step is a
profile of prepare (GUO's added work in it: covering land, the id mirror, the
mesh bookkeeping) before any more draw-side work. The ClassicUO JSON write
first failed (its build turns reflection-based System.Text.Json off); PerfDump
now writes it by hand.

## World prepare (plan, 2026-09-28, desk only, for review)

Prepare in both clients is `GameScene.FillGameObjectList`, and GUO's copy is
upstream's but for one argument (`ChunkMesh.Build` takes no device). The
sorting file carries one deviation (the lights of statics upstream would
have baked). The measured 2.8-4.5x has two known causes before any profile:

1. **GUO was unoptimised** (`Optimize=false`, above). This is a measurement
   defect, not a client defect, and it goes first.
2. **GUO sorts every static; upstream bakes most of them.**
   `ChunkMesh.IsStaticExcludedFromMesh` returns true for everything (ADR-0004:
   with no depth buffer a baked chunk cannot take part in the painter's
   sort), so each visible static goes through AddTileToRenderList,
   CheckIfBehindATree, PushToRenderQueue and the sort, where upstream's mesh
   fast path skips them. That is more work per frame by design, and it grows
   with static density, which fits the bank (4.3x) against the open field
   (2.8x).

Steps, each a small slot (~10 min) or none:

- **P0, measure fairly (1 slot).** perf_dump and perf_probe build GUO with
  `-p:Optimize=true` into the folder the editor runs from, and record it in
  their output (`optimize: true`). Rerun the four-scene ClassicUO comparison
  and one `--merged-land=array --merged-cover` probe. Expected: prepare and
  world draw both fall; how far says how much of the gap is left.
- **P1, work per object (no new slot; rides on P0).** PerfDump also records
  the render-list sizes, read the same way in both clients (the lists are
  the same fields): objects sorted, statics meshed, covering land queued.
  GUO's ms per sorted object against ClassicUO's separates port efficiency
  from ADR-0004's extra statics.
- **P2, profile (1 slot, only if P0 leaves more than ~1.5x per object).**
  `dotnet-trace` on the running GUO process, 10 s at the bank, optimised
  build, top methods under FillGameObjectList. It is a new third-party tool,
  so it gets its own `tools/dotnet_trace/` README (version, origin) and
  nothing installed globally.
- **P3, reduce (design, then code; after P1/P2).** Candidates, cheapest
  first: (a) keep the per-static data prepare recomputes each frame
  (StaticTiles lookups, the behind-a-tree test inputs) on the static
  itself, for statics that do not move, as a GUO-side cache with a PORT
  DEVIATION marker; (b) cull statics wholly outside the viewport before they
  reach the sort, if upstream's bounds test runs later than GUO needs;
  (c) the real fix for cause 2, a depth-tested world pass (ADR-0001), which
  would let statics be baked as upstream does. That is an ADR decision, not
  a B4 task.

Recommendation: P0 at the next free slot, since every absolute number
depends on it; P1 in the same slot; decide P2 and P3 from what they show.
