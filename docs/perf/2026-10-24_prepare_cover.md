# Prepare path and cover submission (review items B and D), 2026-10-24

Two items from the 2026-10-24 performance review: B (the tile walk's
prepare cost) and D (covering land's walk and submission). Item B is the
win. Item D's submission change removes native calls, but the time behind
them did not go away; see below.

## What changed

**B. Chunk skip in the tile walk** (`GameScene.FillGameObjectList`).
Upstream walks every object in a square tile window around the player. At
2560x1440 and zoom 2 that window is about four times the area of the
screen's diamond, and every object in it tests its own screen anchor and
is then rejected. A chunk whose anchors cannot reach `[_minPixel,
_maxPixel]` for any of its 64 tiles at any Z (-128..127, which also bounds
a stretched land's `MinZ`) now skips the walk. Its mesh is still built,
reset and listed, as upstream leaves it. About 550 of the window's chunks
are skipped each frame at the bank, the docks and on the walk.

Upstream refreshes rejected objects' screen positions in frames that move
the view. A chunk skipped in such a frame is marked
(`Chunk.ScreenPositionsStale`) and refreshes every position it visits on
its next walk, before any of its objects can be drawn.

What starts later: alpha fades and name handles of objects in a skipped
chunk. A skipped chunk's objects are at least ~500 world px off screen,
and a fade takes about 220 ms (11 steps of 25 every 20 ms).

`--chunk-skip=off` turns the skip off; `--chunk-skip` makes it the perf
probe's parity toggle. Probe field: `chunks_skipped` (data_formats
section 41).

**D. Cover** (small, exact):
- `CoverFromBelow` takes the tile's land from the walk
  (`RenderLists.GroundHint`) instead of a chunk lookup and a list walk per
  object. A tile holds one land, so it is the same object.
- `CoverRuns` keeps each run mesh's RID.
- `LandPages` keeps each page's scale, instead of two native size calls
  per covering tile.

## Parity

Frozen world (`--perf-parity --chunk-skip`), 2560x1440, zoom 2, five
captures per path: login, open field, bank, forest, dungeon and docks all
show **0 repeatably differing pixels**. At the bank, 843 pixels flickered
once and none repeated, the same transient as earlier runs. The D changes
return identical values by construction; render_sort_tests and smoke
pass.

## Numbers

Release export (`tools/windows/run.py export --release`), 2560x1440, zoom
2, static world, `GUO_PERF_WARMUP_SECONDS=40`, 20 s standing samples,
plus the 12 s walk scene. Three variants were alternated twice:

- **main**: `d4cf8652`
- **cover only**: this branch with `--chunk-skip=off`
- **branch**: this branch as it ships

**Shared host:** other projects held total CPU at 63-82% on average
(peaks 90-100%) during every run. Absolute numbers carry that load.
Treat the variants' ordering, not the absolute numbers, as the result.

Means of the two rounds:

| scene | variant | mean ms | p50 | p95 | p99 | world prepare | gather stage | world draw | cover_land stage |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| bank | main | 13.16 | 13.03 | 17.95 | 19.16 | 5.77 | 5.73 | 3.74 | 0.53 |
| bank | cover only | 15.07 | 15.03 | 19.89 | 21.49 | 6.84 | 6.80 | 4.10 | 0.57 |
| bank | branch | **10.76** | 10.56 | 14.77 | 15.66 | **3.67** | **3.57** | 3.58 | 0.45 |
| docks | main | 11.27 | 11.06 | 15.63 | 17.76 | 4.59 | 4.54 | 3.13 | 0.37 |
| docks | cover only | 11.63 | 11.47 | 15.86 | 17.58 | 5.08 | 5.06 | 2.98 | 0.34 |
| docks | branch | **8.48** | 8.20 | 12.05 | 13.37 | **2.54** | **2.53** | 2.65 | 0.30 |
| walk | main | 8.27 | 8.34 | 14.51 | 17.97 | 2.95 | 2.94 | 3.28 | 0.07 |
| walk | cover only | 7.50 | 7.18 | 13.38 | 16.02 | 2.73 | 2.69 | 2.96 | 0.05 |
| walk | branch | **5.30** | 3.65 | 10.83 | 12.72 | **1.32** | **1.29** | 2.38 | 0.05 |

**The branch beats main in every scene in both rounds.** The gather stage
fell 38% at the bank, 44% at the docks and 56% on the walk. The first
cover-only run was the most loaded of the six (82% host CPU on average,
100% at peak). The cover-only variant cannot be told apart from main
under this load.

## What D's profile showed

A Release CPU profile of main at the bank (dotnet-trace, sampled thread
time) put `Resource.GetRid` inside `UltimaBatcher2D.FlushCover` at ~12% of
the world frame. With the RID cached, the same time shows up in that
run's `CanvasItemAddMesh`, the next native call. So the cost is the main
thread waiting at the engine boundary once per cover run (about 800 runs
a frame at the bank), not the RID lookup itself.

The lever is fewer cover runs. A run ends wherever covering land and
sprites alternate, because the land-array material differs from the
sprite material and each switch cuts a new canvas item. Drawing covering
land under the sprite material (one shader that samples either source)
would remove those cuts without changing draw order. That needs a shader
and its own parity proof, and is proposed as the next item, not done
here.
