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

## Historical exploratory timing

These pre-integration timings were taken on a busy host. They are not
quotable performance gains under the current performance gate. The final
desktop validation below supersedes them for acceptance.

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

## Desktop validation after integration

Measured code `b08783c0e53bfd2f31383cdf0d7dfa0b15a4e62e`, rebased onto
`e88c46983e610f15b49da506d7e5a28e72a47d5b`. Release DLL SHA256:
`90eb90e4a2b0ff53264ccdd9d40a0c5a577b9fb787d49dd17499463914c1c24d`.
Evidence run: `20261024_guo-worker-astra-fps-20261024_desktop-gates`.
This remains an off-by-default canvas renderer prototype; no player option
or default renderer change is introduced.

Correctness fixes reserve the marker size only under the shared material,
route actual triangle-array commands through the original shader (their
vertex format lacks CUSTOM0), and keep the previous item's offset until
pending cover has flushed. Affine quads remain texture-rect commands under
the shared shader. Cover uses the integrated canvas-command wrapper.
Marker images and materials are disposed.

Full smoke and Release export passed. Both exported renderer runs (default
and `--shared-cover`) passed 1,243 checks, with zero renderer errors and the
existing one CanvasItem / one ObjectDB leak warnings. The added checks cover
real marker-sized sprites, following ordinary sprites, batched/mirrored
fallbacks, transformed UI clipping and queued cover across an offset change.
The perf tool tests passed 127 checks.

At 2560x1440, zoom 2, Forward+, all six frozen scenes (login, open field,
bank, forest, dungeon, docks) had **zero strict and zero repeatable pixel
differences** in each of three separate comparisons:

- Original versus new sprite shader, with cut submission on both sides.
- Cut versus shared cover submission under the new shader.
- Shared toggle with the Silhouettes postfx/id-mirror fallback active.

One warmed Release off/on pair used the same DLL, plain profile, quiet
static fixture, 40-second warm-up, 20-second standing samples and 12-second
walk. The six-scene count gate passed; docks also passed its limits.

| scene | items off -> on | draw calls off -> on | mean ms off -> on | p95 ms off -> on | p99 ms off -> on |
|---|---:|---:|---:|---:|---:|
| bank | 1608 -> 36 | 1568 -> 1629 | 6.907 -> 6.246 | 9.791 -> 8.442 | 12.735 -> 10.504 |
| forest | 454 -> 36 | 468 -> 475 | 4.172 -> 3.832 | 5.614 -> 4.833 | 7.125 -> 5.862 |
| dungeon | 1000 -> 40 | 1003 -> 1018 | 3.176 -> 2.713 | 4.539 -> 3.572 | 5.951 -> 4.238 |
| docks | 1606 -> 36 | 1560 -> 1628 | 8.489 -> 5.488 | 14.450 -> 7.550 | 15.502 -> 9.564 |
| walk | 416.4 -> 37.7 | 433.3 -> 438.6 | 2.463 -> 2.226 | 3.409 -> 3.088 | 4.332 -> 3.888 |

**These times do not establish a gain or tail-latency acceptance.** Other
process CPU averaged 17.5-58.5% across the measured scenes, exceeding the
10% quiet-host threshold throughout. The full report includes host mean/max
and all seven rows. A second alternating pair was deferred until a quiet
window is available; repeating the noisy measurements cannot clear that gate.
Draw calls rose slightly (up to 4.36% at docks), rather than falling.

Promotion remains blocked on repeated quiet-host timing and tail gates.
The following follow-up covers the independent desktop checks; phone,
Deck and web remain outside this evidence. No default enablement or
subsequent bottleneck implementation is included.

### Independent desktop follow-up

Evidence run: `20261024_guo-worker-astra-fps-20261024_desktop-interactions`.
The same b08783c0 Release DLL above passed all six frozen scenes at each
of zoom 1.4, 0.8 and 0.5: zero strict and repeatable differences.

The existing inn at map0, 1451,1707,0 has carpet at z0 and slate roof at
z23-26. With DrawRoofs true, its interior shows automatic roof removal.
Six strict-login runs compare shared off/on with circle of transparency
disabled, gradient mode enabled, and shader-circle mode enabled. Each
uses an isolated temporary profile, 1280x720, zoom1.4, five captures.
The inn-outline comparison has 125,360 stable pixels and zero differences
for all three pairs. The effect control changes 28,233 inn pixels in
gradient mode and 27,791 in shader-circle mode, equally on both paths.

The original bounding rectangle also includes neighboring buildings:
it has zero, five and thirty differences respectively. All residuals
are outside the inn at x596-605,y705-709; their cause is not proven.
Both comparisons and the outline are retained in `house-comparison.json`.
This is a dedicated interior check, not whole-viewport frozen parity.

The existing exported highlight probe passes all four checks on both
paths: injected mouse position (700.95557,378.52914) selects the same
Land0x0408 at1454,1706, the chunk unloads after teleport, and its reloaded
mesh has no stray highlight. This proves hover picking and cleanup,
not click/pickup behavior. No account, character or world fixture was
created or modified beyond moving the allocated probe character.

### PR156 interaction on an isolated branch

`work/astra-shared-cover-pr156-20261024` merges shared-cover source
`01f09b35` and original PR156 head `8555fc84` in `101a0d52`, preserving
both parents and the original refs. The newer PERF-LANE-01 timing/replay
protocol supersedes PR156's earlier timing switches; its renderer counters
and tests are retained. No public merge or default enablement is included.

The first combined renderer probe exposed two retained visibility count
failures: short MultiMesh runs submitted raw rectangles without recording
their ownership. The integration fix uses the existing CanvasRect and
CanvasAffine helpers for replay, and explicitly bypasses retention before
long MultiMesh commands, whose pooled buffer changes independently of its
RID. The expanded probe checks instanced and replayed runs with retention
off and on across six transforms. Full smoke and Release export passed;
all three exported modes (default, shared, shared plus MultiMesh) passed
1,315 checks each, with zero renderer errors and the existing one
CanvasItem / one ObjectDB leak warnings. The perf-tool suite passed 127
tests and documentation lint passed 334 documents.

Final integration Release DLL SHA256:
`813b8f518f4764733867310e29b8d7827f6556146de7235c99f0f2209260cd8d`.

At 2560x1440, zoom2, Forward+, all six frozen scenes have zero strict
and repeatable differences in three comparisons: MultiMesh toggled over
shared cover, shared cover toggled over MultiMesh, and retained canvas
toggled with both enabled. The retained diagnostic reports its target
exercised. See `v2-*-manifest.json` and `final-gates.json` in the evidence
run; the original shared-only Release DLL remains unchanged.

MultiMesh uses a different material, so shared cover deliberately falls
back to cut submission when both are enabled. At the bank, the combined
mode uses 1,608 items and 1,589 draw calls, versus shared-only's 36 items
and 1,629 draws. Flipping shared while MultiMesh is active leaves counts
unchanged. This proves fallback compatibility, not combined savings.
The original shared-only bank reduction remains 1,608 to 36 items with
draw calls increasing from 1,568 to 1,629 (+3.9%). Quiet-host repeated
alternating Release A/B and tail gates remain necessary for gain claims.
