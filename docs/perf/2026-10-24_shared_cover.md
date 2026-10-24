# Canvas-item cuts and the shared-cover prototype, 2026-10-24

Follow-up to `2026-10-24_prepare_cover.md`. That profile found the cost of
covering land at the engine boundary, once per cover run. This report
counts why the batcher starts canvas items, then tries drawing covering
land without starting new ones.

## Step 1: counters

New probe fields (data_formats section 41):

- `cuts_total`, plus a count per reason (`cuts_<reason>`):
  - `cover`: FlushCover and DrawMeshSprite
  - `material`: EnsureMaterial, blend states, mesh layers
  - `offset`: world offset, UI transform
  - `sampler`
  - `clip`
  - `begin`
  - `other`
- `items_allocated`: the batcher's pooled canvas items.

Zoom 2, 2560x1440, static world:

| scene | cuts | of which cover | items used | cover runs |
|---|---:|---:|---:|---:|
| bank | 1,601 | 1,572 | 1,608 | 786 |
| forest | 447 | 418 | 454 | 209 |
| dungeon | 992 | 960 | 1,000 | 480 |
| docks | 1,599 | 1,570 | 1,606 | 785 |

Every cover run costs two cuts: one onto the land-array material and one
back. Everything else in the world frame uses about 30 items.

## Step 2: `--shared-cover` (prototype, off by default)

`uo_hue_cover.gdshader` is `uo_hue.gdshader` plus the land array's
sampling. It loads onto the sprite material when the flag is given. A
cover run is then drawn as one more command on the current sprite canvas
item, in its place among the sprites, with no cut. Painter order and
command boundaries are unchanged: sprite, land, sprite stays exactly that.
The offset between the cover and the item rides on the command's
transform.

**Telling cover from sprites.** The shader must decide per command, and
`CUSTOM0` is only defined on meshes. A cover mesh is drawn with a marker
texture of 2 x 4096. Both sides are powers of two, so its
`TEXTURE_PIXEL_SIZE` is exact. Only that texture size selects the land
branch, which reads `CUSTOM0` as `uo_hue_land_array.gdshader` does
(normal in xyz, page in w). For everything else the shader is uo_hue line
for line. Under any other material (blend states), the old cut path is
used.

**Parity at other zooms:** bank, dungeon and docks also show 0 repeatably
differing pixels at 1.4 (asked 1.37, the nearest step), 0.8 and 0.5. At
0.5, 18 bank pixels flickered once and none repeated.

**Parity:** frozen world, `--perf-parity --shared-cover`, zoom 2. The
toggle compares the cut path against the shared path, both under the new
sprite shader. Login, open field, bank, forest, dungeon and docks all
show **0 repeatably differing pixels**; the dungeon had 2 one-off
flickers.

**Items:** bank 1,608 -> 36, docks 1,606 -> 36, forest 454 -> 36,
dungeon 1,000 -> 40. Engine draw calls are unchanged (~1,600 at the bank):
the renderer still breaks batches where the texture changes.

## Release timing

One Release export, `--shared-cover` off vs on, alternated twice, with a
40 s warm-up, 20 s standing samples and the 12 s walk. Average host CPU
fell over the session (65%, 47%, 43%, 46% for runs 1 to 4), so round 1
favours "on". Round 2 is the matched pair:

| scene | world draw off -> on | mean ms off -> on | p95 off -> on | p99 off -> on |
|---|---|---|---|---|
| bank | 2.57 -> 2.42 | 7.39 -> 6.99 | 11.26 -> 10.28 | 13.36 -> 12.11 |
| docks | 2.36 -> 2.08 | 7.22 -> 6.32 | 11.82 -> 10.29 | 13.35 -> 12.00 |
| walk | 1.83 -> 1.49 | 4.08 -> 3.34 | 8.69 -> 7.18 | 11.49 -> 9.63 |

On beat off in every scene in both rounds, and no frame tail got worse.
The gain is in submission: world draw and the statics stage, which now
carries no item switches. Engine-side render CPU is unchanged (~0.07 ms).

## Not yet covered (the prototype's gate)

Proven: the six standing scenes and login at zoom 2, and bank, dungeon
and docks at 1.4, 0.8 and 0.5. Not run yet:

- the id mirror (post-processing on): it turns merged cover off, so it
  takes the old path
- clipping (cover draws only in the world pass)
- picking (CPU-side, unchanged)
- roofs and the circle of transparency in a house
- `--multimesh-sprites` on top (#156)
- phone, Deck and web

Off by default until those are done and the owner decides.
