# Parity night, 2026-09-25/26

Overnight ClassicUO vs GUO comparison at places the regular `ab_compare` set does
not cover, plus the reports from the evening's play session. Shots are under
`build/screenshots/ab_night/<place>/`, `ab_night2/<place>/` (dungeons and
night), `ab_doors/<place>/`, `ab_zoomout/<place>/`, `ab_facets/<place>/`,
`ab_regions/<place>/`, `ab_volcano_dump/<place>/`, `ab_lava_night/<place>/`,
`ab_towns_night/<place>/`, `ab_lightcap_fix/<place>/`, `ab_night3/<place>/` and
`ab_indoor_night/<place>/` (`cuo.png`, `guo.png`,
`ab.png`); door frames under `door_probe/<place>/`.

The working tree this ran on held two sets of uncommitted changes together:
this session's (listed under Changes) and another session's "cached statics
join the world sort" (ADR-0004 amendment 2026-09-26, `godot/GUO/dev/`). Every
GUO picture below is of both.

Every sweep ran at zoom 1.0: the shared profile's `default_scale` is 1, and
`ab_compare` copies GUO's profile into ClassicUO, so both clients drew the same
view.

## Reports from play

| Report | Status |
|---|---|
| Player drawn under the tree in front of it | fixed, T1 |
| Trees drawn over roofs | the other session's change (cached statics join the sort) plus T1 |
| Ctrl+wheel zoom works only sometimes | fixed, Z1 (built, not tried by hand) |
| Frame rate drops zoomed out | improved, P1; still well behind ClassicUO fully zoomed out |
| Door opened by an NPC pops over the roof | not reproduced, D1, not in stills nor frame by frame |

## Changes

- **T1, fading objects join the depth sort** (`RenderLists.Add`). Anything with
  AlphaHue below 255 -- foliage faded because it hides the player, a roof
  fading out, a sprite fading in -- went to `_transparentObjects`, drawn after
  the whole world. Upstream draws that list last too, but still inside
  `GameScene.DrawWorld`'s `SetStencil(DepthStencilState.Default)`, so the depth
  test keeps it behind whatever is in front of it. Here it painted over the
  player and over roofs. Everything but land now joins `_world` at its own
  depth. Checked with GUO shots in front of, behind and beside an oak at Yew
  (`build/screenshots/trees/`), and the other session's
  `regression_probe -- rendering` still passes.
- **Z1, Ctrl+wheel zoom** (`GodotInput.ToKeymod`). `Keyboard.Ctrl` is set only
  from key events. SDL counts a modifier key's own press in that event's mod;
  Godot on Windows did not, so Ctrl read false until key repeat began and the
  wheel zoomed only if Ctrl had been held a while. The modifier key's own state
  is now folded in on press and cleared on release. Built; not yet tried by
  hand.
- **W1, land drawn in depth order** (`ChunkMesh`). Each chunk's land was
  bucketed by texture for the mesh; it is now ordered by `CalculateDepthZ()`,
  grouped by texture only among equal depths. See W1 below. Costs 30-65% more
  draw calls at the minoc mines and no measurable time (world draw and render
  CPU unchanged within noise, zoom probe with and without).
- **K1, the world target starts black** (`UltimaBatcher2D.SetRenderTarget`).
  See K1 below.
- **P1, covering land keeps its mesh** (`MeshLayer.GetSpriteMesh`,
  `UltimaBatcher2D.DrawMeshSprite`). A land tile drawn again by
  `CoverFromBelow` rebuilt a one-quad `ArrayMesh` every frame -- a new Godot
  array, four copies and a surface upload per tile. The layer now keeps one
  mesh per sprite with the quad it was built from, and rebuilds only when the
  quad differs (compared, not dirty-flagged, because ChunkMesh and the probes
  write quads too).
- **P1, texture metrics read once per texture** (`UltimaBatcher2D.Measure`).
  Every sprite asked Godot for its texture's width, height and RID -- five to
  seven engine calls per sprite. The last texture's are kept until it changes.
  World draw fully zoomed out at the mines 45 -> 41 ms.
- **P1, shadows as one rect under a transform** (`UltimaBatcher2D.TryAddAffineQuad`).
  A static's shadow is a parallelogram, which went through
  `canvas_item_add_triangle_array` -- four managed arrays marshalled per call,
  a sixth of the frame zoomed out. A quad that is the image of its texture
  region under an affine map (every shadow, every mirrored sprite) is now one
  `texture_rect_region` between two `set_transform` commands; anything else
  still takes the triangles. GUO shots before and after at yew-fence-oak,
  minoc-mines and britain-bank differ only in animals that moved.
- **V1, additive blending in hardware** (`UltimaBatcher2D.SetBlendState`,
  `BlendState.IsAdditive`, `shaders/uo_hue_add.gdshader`). See V1 below.
- **L1, static lights added in upstream's order** (`ChunkMesh.UpstreamWouldMesh`,
  `GameSceneDrawingSorting.ProcessStaticLikeTail`, `StaticView.Draw`,
  `MultiView.Draw`). See L1 below.
- **DoorProbe** (`--door-probe`, `src/Bootstrap/DoorProbe.cs`): see D1.
- **ZoomProbe** (`--zoom-probe`, `src/Bootstrap/ZoomProbe.cs`): per zoom step,
  wall-clock frame time over 240 frames, world prepare, world draw, the other
  profiler contexts, render CPU/GPU, draw calls, the size of each render list,
  and GC.

After all of the above: `regression_probe -- rendering` passes, and the
headless groups (atlas, json, tcp, websocket, video) pass.

## Findings

### K1 -- past the edge of the map, grey instead of black

- **Where:** hythloth (the dungeon sits against the top edge of the map). GUO
  drew a flat (17,17,17) where ClassicUO has (0,0,0); shame and wrong have a
  corner of it too.
- **Cause:** upstream's render targets are `RenderTargetUsage.DiscardContents`,
  and FNA clears such a target to opaque black whenever it is bound (release
  `DiscardColor`, `GraphicsDevice.SetRenderTargets`). The light and UI targets
  are cleared again straight after; the world target is not, so it is black
  wherever nothing is drawn. GUO's world target started transparent, and the
  window background, tiled at 10% (`RenderTargets.Draw`), showed through.
- **Fixed:** binding a target now sets its clear colour to opaque black, as FNA
  does. Hythloth reshot: the region is (0,0,0); shame and wrong differ from
  before only there and in monsters that moved.

### P1 -- frame rate zoomed out

Read off each client's own FPS counter in the first sweep, same 3840x2054
window and profile, before any of the P1 changes: ClassicUO 60 everywhere,
GUO 16-61. Places with water or sunk ground were the slow ones.

| GUO FPS (before) | Places |
|---|---|
| 16 | minoc-mines |
| 23-25 | serpents-hold, britain-bank, papua |
| 30-41 | buccaneers-den, trinsic, magincia, britain-graveyard, vesper, jhelom |
| 49-57 | covetous-mouth, delucia, nujelm, cove, compassion-shrine, wrong-mouth |
| 60-61 | moonglow, ocllo, skara-brae, shame-mouth, castle-throne, yew-fence-oak |

The first cause was covering land (the mesh rebuild above): time tracked the
number of covering tiles, not the size of the scene.

| minoc-mines, zoom 0.7 | covering land | world draw | process |
|---|---:|---:|---:|
| before | 1919 | 28.0 ms | 133 ms |
| after the mesh cache | 1919 | 3.9 ms | 25 ms |

Wall clock at the mines (`--zoom-probe`, frames per second, vsync caps at 60):

| zoom | after mesh cache + texture memo | + affine shadows |
|---:|---:|---:|
| 0.5 - 0.7 | 60 | 60 |
| 0.9 | 55 | 59 |
| 1.1 | 41 | 47 |
| 1.5 | 25 | 29 |
| 2.5 | 10 | 12 |

**Still open.** Fully zoomed out (2.5) ClassicUO runs the same view at 37-39
FPS (`ab_zoomout` sheets, its own counter) and GUO at 11-12. At 2.5 the mines
frame is about 85 ms: world prepare 32 ms, world draw 29 ms, the rest of
RENDER_FRAME 2 ms, and about 20 ms outside the client's counters -- Godot
itself, turning ~17,000 draw calls a frame into GPU work (the GPU is at about
1 ms). GC is nil (40 KB allocated a frame, no collections).

Both halves scale with the ~33,000 sorted objects and each object costs at
least one native call, where upstream appends vertices to an array. Tried and
dropped (no measurable change, reverted):

- one repeating `texture_rect` for the tiled window background instead of a
  Draw per tile -- a trace had put it at 8% of the frame; timing it said under
  2 ms;
- covering land drawn inside the sprites' canvas item instead of cutting two
  items to switch material -- draw calls stayed at 16,900, because they come
  from texture changes, not from items.

What would close the gap is structural: building the sorted world into
per-frame meshes grouped by texture page (the way the land mesh already is),
so a frame costs hundreds of native calls, not tens of thousands. That changes
ADR-0004's drawing model and wants an ADR of its own before any code.

`dotnet-trace` at 2.5 (main thread, inclusive): DrawWorld 81-86%,
FillGameObjectList 33-40%, AddSprite 28-33%, Static.Draw 26-33%, the `_world`
sort 5-6%, CoverFromBelow 5-6%, NewItem/Cut 6%. Native ptrcall is ~57% of the
main thread. Treat the per-method figures as rough: the trace attributes 8% to
the tiled background, which timing does not bear out.

### W1 -- lower land painted over the land in front of it

- **Where:** trinsic (the canal in the park: water spills over the cliff banks
  in tile-shaped steps) and minoc-mines (a sawtooth of water over the green
  bank, top right). ClassicUO keeps both banks whole.
- **Cause:** the land mesh is sorted by texture (`MeshLayer.BuildVisibleIndices`
  keeps each chunk's texture runs, and chunks are drawn in loop order). Upstream
  sorts it the same way and lets the depth buffer settle overlaps; here the
  submission order is the answer, so a low water tile can land after the higher
  bank in front of it. F1's `CoverFromBelow` only handles statics and items
  below their own tile's ground, not land below the land in front of it.
- **Fixed** by drawing each chunk's land in depth order (Changes, W1). Reshot
  at minoc-mines, trinsic, papua and buccaneers-den: the banks are whole. The
  black marks at buccaneers-den are fins, in both clients.
- Across chunks the order is still the chunk loop's. No place in the sweeps
  shows a seam from it.

### V1 -- overlapping lights did not add up

- **Where:** the Volcano (Ter Mur). The lavafall and the rock around it are
  bright in ClassicUO and dull brown in GUO, with rings where lights meet.
  Two GUO shots minutes apart agreed, so not an animation frame.
- **Found by** a render dump in both clients beside the fall
  (`GUO_RENDER_DUMP_DIR`, `renderdump`): the same objects, hues, alpha and
  order in both. The lava floor under the fall (0x1318) is a LightSource,
  about 90 tiles of it within a few screens.
- **Cause:** the light pass draws every light under `BlendState.Additive`.
  GUO did every non-default blend by hand (`uo_hue_blend`, ADR-0003): one
  back-buffer copy, then each sprite reads it as the destination. Every
  light after the copy read the same destination, so where lights overlapped
  each replaced the one before instead of adding to it. Lamps in a town
  rarely overlap, which is why the night sweeps matched.
- **Fixed:** XNA's Additive is (SourceAlpha, One) on colour and alpha, which
  is Godot's own `blend_add`. The batcher now gives it a hardware material
  (`uo_hue_add`) with no read-back. Two overlapping half-alpha quads on a
  transparent target measure (0.596, 0.400, 0.196, a 0.494) against XNA's
  (0.6, 0.4, 0.2, a 0.5). The lightning effect and the world map's additive
  pass take the same path.
- **Checked:** reshot beside the fall, GUO now matches ClassicUO -- the fall's
  brightness and the halo on the rocks (`ab_volcano_dump/volcano-fall/`).
  At fire, hythloth, deceit, trinsic-night and britain-night GUO moved
  closer to ClassicUO or stayed put (mean difference in the world area, e.g.
  trinsic-night 4.51 -> 4.41, deceit 1.84 -> 1.77). Fire and hythloth shot
  again at light 26 (`ab_lava_night/`), where the lava lights carry the
  scene: the halos along every lava bank match. `smoke`,
  `regression_probe -- rendering` and `batcher_probe` pass.

### L1 -- Nujel'm's lamp posts lit where ClassicUO's are not

- **Where:** Nujel'm at light 26. GUO drew pools of light round the lamp posts
  in the streets; ClassicUO drew none there, only the lit windows. Every other
  town in the night sweep matched.
- **Found by** a render dump (same objects both sides) and then a temporary
  count in `GameScene.AddLight`: 331 lights offered a frame, the list keeps
  `LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT` = 100 and drops the rest. 47 of
  the offers were items (the lamp posts), and 32 of those arrived while there
  was still room. Magincia: 299 offered, 15 items, 8 in; Britain's bank: 31,
  nowhere near the cap.
- **Cause:** which 100 survive depends on the order lights are offered.
  Upstream offers a baked static's light while sorting, before anything is
  drawn, and an item's while drawing it -- so in a town full of lit windows the
  statics fill the list and the lamp posts never get in. GUO bakes no statics
  (the deviation in `ChunkMesh.IsStaticExcludedFromMesh`), so every light was
  offered while drawing, in depth order, and the near lamp posts made the cut.
- **Fixed** (PORT DEVIATION (GUO)): `ChunkMesh.UpstreamWouldMesh` answers
  upstream's baking rules (allowed to draw, not destroyed, a multi only in
  state 0, not internal, animated, foliage, tree or rock, has art). The sort
  offers such a static's light where upstream's mesh fast path does, with the
  same exceptions (fading out, gradient circle of transparency), and
  `StaticView`/`MultiView.Draw` no longer offer it a second time.
- **Checked** (`ab_lightcap_fix/`, ClassicUO's shots from `ab_towns_night/`):
  Nujel'm mean brightness 26.71 -> 23.46 against ClassicUO's 23.47, no lamp
  pools, the same windows lit. Magincia 24.66 -> 24.64 (ClassicUO 24.74) and
  Britain's bank 19.02 -> 19.02 (19.05). The count was taken out again.
  `smoke`, `regression_probe -- rendering` and `batcher_probe` pass.

### D1 -- door opened by an NPC over the roof: not reproduced

Every door at five places opened from the shard
(`[Screen set Open true where BaseDoor`) and shot in both clients
(`ab_doors/`): britain-street, britain-west, trinsic, vesper, minoc. GUO
matches ClassicUO at all five -- open doors stay behind the roofs in front of
them.

A still picture cannot catch a pop that lasts a few frames, so a second probe
(`--door-probe`, `src/Bootstrap/DoorProbe.cs`) shuts and opens every door on
screen twice and keeps the 40 frames after each command
(`build/screenshots/door_probe/<place>/`). At britain-street and trinsic each
frame was diffed against the settled last frame of its cycle: the door changes
in a single frame about 20 frames after the command (the round trip to the
shard), and the only pixels that differ are the door's own, plus NPCs and
animals walking. Nothing is drawn over a roof at any point.

The most likely cause of what was seen is the old separate statics mesh pass,
which the other session's change removed. The doors on the dev shard were left
open.

## Places

| Sweep | Places | Result |
|---|---|---|
| `ab_night` | trinsic, vesper, moonglow, skara-brae, jhelom, magincia, nujelm, serpents-hold, cove, buccaneers-den, ocllo, papua, delucia, covetous-mouth, wrong-mouth, shame-mouth, britain-bank, castle-throne, britain-graveyard, yew-fence-oak, minoc-mines, compassion-shrine | match, except W1 (trinsic, minoc-mines; fixed) and frame rate |
| `ab_night2` | despise, deceit, destard, covetous, hythloth, shame, wrong, fire | match, except K1 (hythloth, corners of shame and wrong; fixed) |
| `ab_night2`, light 26 | trinsic-night, britain-night | match; light pools, lamp posts and water line up |
| `ab_doors` | britain-street, britain-west, trinsic, vesper, minoc | match, D1 |
| `ab_zoomout` | minoc-mines, moonglow at zoom 2.5 | same picture; ClassicUO 37-39 FPS, GUO 10-12 |
| `ab_facets` (`[go <region>`) | montor, mistas (Ilshenar); luna, umbra, doom, bedlam (Malas); zento (Tokuno); underworld, volcano (Ter Mur) | match, except V1 (volcano; fixed) |
| `ab_regions` (`[go <region>`) | wind, ice, khaldun; labyrinth, paroxysmus, homare, isamu all four shot the same town (the names did not all resolve) | match |
| `ab_lava_night`, light 26 | fire, hythloth | match (after V1) |
| `ab_towns_night`, light 26 | vesper, moonglow, skara-brae, jhelom, magincia, nujelm, serpents-hold, buccaneers-den, britain-bank, castle | match, except L1 (nujelm; fixed, `ab_lightcap_fix/`) |
| `ab_night3`, light 26, after L1 | luna, zento (`[go <region>`); minoc, yew, cove, ocllo, delucia | match; mean brightness within 0.2 of ClassicUO at every place |
| `ab_indoor_night`, light 26, after L1 | inside the banks at trinsic, vesper, moonglow, jhelom, britain, skara-brae | match; roofs hidden alike, lights line up, mean brightness within 0.2 |

Differences read and dismissed: monsters, animals and NPCs that moved between
the two shots; the FPS counter; a tooltip GUO shows over the status gump;
light-source brightness at deceit's pentagram (braziers flicker).
