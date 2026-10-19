# ADR-0037: Water render modes

## Status

WATER-05 amendment (owner request, 2026-10-19): retain the three WATER-04
packs and add the data-only `guo-water-classicplus` pack, displayed as UO+.
Its built-in model 5 preserves native water texels with a subtle dithered
shimmer and shore lap. All styles use a continuous low-shore contour instead
of independent tile-edge ramps; Classic's branch and sampling are unchanged.
The `guo/water@1` model enumeration is extended in section 44 and both validators.

Proposed — 2026-10-18 (WATER-01). Needs the owner's decision before WATER-02.

## Date

2026-10-18

## Decision Makers

The owner (asked for realtime water with styles and shard control,
2026-10-18), relayed by the director (guo-director).

## Summary

An optional **water render mode** replaces how water tiles are shaded, not how
they are sorted or picked. It is an L1 client option (default **Classic**:
today's tiles, pixel for pixel) under ADR-0027's world render modes. The look
of each style comes from a Store `water` component (L2); classic is the
built-in zero pack. Objects on water write into a small ripple texture the
water shading reads. A shard can say which styles its players may use.

## Context

### How water is drawn today (measured 2026-10-18 on `main` 96ce0ff2)

- Water is anything tiledata flags **Wet** (`TileFlag.Wet`, 0x80). Nothing in
  the client special-cases water ids.
- **Wet land** (0xA8–0xAB, "water") has no texmap, so `Land.Create` never lets
  it stretch: it is flat land art, drawn in the chunk's land mesh (ADR-0004)
  with `uo_hue_mesh`, land first, before the sorted pass.
- **Wet statics** (0x1796–0x17B2, "water") are ordinary statics: sprites in the
  depth-sorted pass (`StaticView` → `View.DrawStatic`) with `uo_hue`, ordered
  by `CalculateDepthZ()` like everything else (ADR-0001). Statics never enter
  the mesh (ADR-0004, 2026-09-26 amendment).
- Near the coast **most water is statics, not land.** Counted from the map
  files in a 48 × 48 cell window on Felucca:

  | Spot | Cells | Wet land | Cells with wet statics | Static z |
  |---|---:|---:|---:|---|
  | Britain docks (1490, 1755) | 2304 | 452 | 1197 (56 stacked two deep) | −5 |
  | Trinsic bay (2070, 2860) | 2304 | 613 | 1073 (55 stacked) | −15, −5, 5 |
  | Britain bank (1438, 1690) | 2304 | 0 | 7 | — |

  The statics sit over blank ("NoName") land. A water look applied to the land
  mesh alone would leave the harbours classic.
- The only water feature in the client is upstream's **Animated water effect**
  (`Profile.AnimatedWaterEffect`, default off, ported as is): it pulls wet land
  out of the mesh and draws every wet tile twice, the second copy scaled by a
  sine on the CPU. It is the only "animated water" in the upstream we track
  (pin 007ef8c3; commits c3c495a6d, a0de16181, 4a540af63, January 2023). The
  ClassicUO 2.0.0 beta's water ships only as closed binaries (a Rust core on
  wgpu/WGSL, per the upstream lane's UP-01 report); no public commit has its
  water or terrain code. Nothing is ported from it; the beta is at most a
  by-eye look reference, and it is not decompiled or copied.
- No world shader reads `TIME` today. All world materials are created once in
  `UltimaBatcher2D` and share `uo_hue_core.gdshaderinc`; global uniforms go
  through `SetOnAllMaterials`. The shader mode travels in the packed vertex
  colour (ADR-0004), so a mesh run and a sprite batch can mix modes without a
  material switch.
- The editor's World view (ADR-0015) draws with the game renderer and sets
  `Time.Ticks` from Godot, but never runs `GameScene.Update`, so animated
  statics and boats stand still there.

### Constraints

ADR-0001's seam (one presenter, `CalculateDepthZ()` the only depth), ADR-0004
(the land mesh, mode byte in the vertex colour), AGENTS.md's L1 and L2 rules,
nearest-neighbour sampling of native art (rule 7), and a perf budget of
**at most 1 ms added** at the Britain docks on the 2026-09-27 baseline
machine (`docs/perf/2026-09-27_baseline_1280.md`).

## Decision

1. **A shader mode, not a render path.** Water styles add one shader mode,
   `SHADER_WATER`, to `uo_hue_core`. The batcher writes it into the vertex
   colour for a Wet land cell in the chunk mesh and for a Wet static sprite in
   the sorted pass, only when the active style is not Classic. Ordering,
   depth, picking, the render lists and the dumps' `drawn` field are
   unchanged: water is drawn exactly where and when it is drawn today, with a
   different fragment colour. No new material, viewport, depth buffer or
   presenter.
2. **The water fragment.** `SHADER_WATER` samples the tile's own art (so hues,
   light and the season's palette still apply), then mixes in the style:
   animated normals scrolled by `TIME`, a flow and ripple texture, and
   shoreline foam where the water mask says a neighbour cell is dry. The
   fragment needs the tile's world cell; it is recovered from the screen
   position, the camera offset and the tile's z, which the batcher already
   knows. If that proves lossy for stacked statics, WATER-02 carries the cell
   in the vertex data instead and amends this ADR. Native art is still
   sampled nearest; only the style's own textures (normals, foam, flow) may
   filter, and a pixel-art style uses nearest for those too.
3. **Classic is the built-in zero pack** and the default: with it selected the
   batcher never writes `SHADER_WATER` and the shader never takes the branch.
   That is the parity guarantee: a render dump and a screenshot with Classic
   must equal `main` (`render_diff` `max_drawn_diff: 0`, and a pixel diff of
   the frame at the docks and the bay).
4. **Upstream's Animated water effect stays as ported.** With a water style
   other than Classic active, the style replaces it (the batcher ignores the
   option and keeps wet land in the mesh); with Classic it behaves exactly as
   today. The Options entry says so.
5. **Ripples.** One `RG8` ripple texture covers the visible area as a ring
   buffer in world cells (128 × 128 cells, 4 texels per cell; 256 KB). Each
   frame the CPU stamps disturbances for objects on wet cells: mobiles (the
   player first), multis that move (boats, through `BoatMovingManager`, with a
   wake along the travel direction), and items or effects that land on water.
   A cheap two-step wave update runs in a tiny canvas pass or on the CPU,
   whichever WATER-03 measures cheaper. One global uniform binds it to all
   world materials. This replaces the brief's per-chunk flow texture: one
   texture means no per-chunk texture switch, which the batcher counts as a
   batch break. Ripples are cosmetic: they never feed movement, targeting or
   the server.
6. **Styles are Store `water` components (L2), data only.** A component
   carries `schema: "guo/water@1"`, a style id, a **built-in shading model**
   (`uo`, `pixel`, `cel`, `realistic`), normal and flow textures (PNG, size
   capped), a foam mask, numeric params (speed, scale, tint, foam width,
   specular) and ripple settings (strength, decay, wake length). Packs do not
   carry shader code in v1: the four models live in the client's shader, and
   a pack picks one and feeds it. (A postfx pack may carry `.gdshader`,
   ADR-0019 Amendment 3, but a water shader would run inside the world
   material, where a bad one could break hue, light and picking parity. A
   later amendment can open this up.) The publisher (`tools/asset_store`) and
   the installer (`StoreContent`) validate the same rules, with corpus cases
   for an accepted pack and each refusal. `docs/asset_pack_ecosystem.md` and
   `docs/data_formats.md` gain the component before anything emits it.
   *Amended 2026-10-19 (WATER-04):* v1 carries numbers only. The normal,
   flow and foam textures are left out: all four models make their own
   surface procedurally, and an image the shader samples would be one more
   filtering question under rule 7. A `grain` number (whole world pixels per
   water pixel) is added instead, for the pixel model. Ripple settings are
   strength and damping; wake length stays the client's. A later amendment
   can add textures as declared PNGs.
7. **The option.** `Profile.WaterStyle` (string, default `"classic"`) and an
   Options entry, **Water**, in both Options interfaces, listing Classic plus
   the styles of installed, verified packs that the shard allows. Changing it
   dirties the chunk meshes (the renderer does it on the frame it sees the
   change). The editor's World view gets the same choice in its Layers menu.
   No `PlatformDefaults` entry or version bump: Classic is the default on
   every platform, the field's initialiser gives it to new and old profiles
   alike, and an entry whose platform value equals the desktop default changes
   nothing (amended 2026-10-19, WATER-03).
8. **Shard control, with consent.** Until STORE-02's content negotiation
   lands, a shard's server pack (`Data/GUO/server-content.json`) may carry a
   `water` block: `allowed` (style ids), `default`, `lock`. The client offers
   the default once and the player accepts or keeps their own; `lock` hides
   styles the shard does not allow, and never installs or downloads a pack.
   The editor's Admin tab writes the block (WATER-05). When STORE-02 exists
   the block moves onto its negotiation.
9. **Perf.** Budget at most 1 ms mean frame time added at the Britain docks on
   the baseline machine, measured with `tools/perf_probe`, which gains a
   `britain-docks` scene (1490, 1755) and a `trinsic-bay` scene. Every water
   PR reports both scenes with the style off and on. Classic must show no
   change beyond run-to-run noise.

## Alternatives considered

- **A separate water layer or SubViewport drawn under the world.** Rejected:
  the harbour water is statics interleaved in the depth sort with piers,
  banks and boats (ADR-0004's cover-from-below rules exist for exactly that).
  A separate layer breaks the order ADR-0001 keeps in one place.
- **Shading only the land mesh.** Rejected by the counts above.
- **A new `water` material per chunk.** Rejected: ADR-0004 already declined
  a land-only material because one run mixes tile kinds; a mode in the shared
  material keeps batching.
- **Pack-supplied water shaders.** Deferred (decision 6).
- **Extending upstream's Animated water effect.** Rejected: CPU double draw,
  pulls water out of the mesh, and is a ported file's behaviour (L0).

## Consequences

- One more branch in `uo_hue_core` that Classic never takes; one global
  ripple texture and a `TIME`-driven uniform set per frame.
- `SHADER_WATER` objects are still native-art draws in the dumps, so the
  render-diff tooling needs no change.
- The editor World view runs the water fragment but, like animated statics,
  shows no ripples unless the embedded scene's update runs; WATER-03 decides
  whether to tick the ripple stamp there.
- Frames with any style other than Classic are not parity frames and are
  labelled as such in evidence.

## ADR Dependencies

ADR-0001 (presenter seam, one depth source), ADR-0004 (land mesh, mode byte
in the vertex colour), ADR-0015 (editor World view), ADR-0019 (Store, and
Amendment 3 on shader code in packs), ADR-0023 (post-processing, which stays
a separate screen pass), ADR-0027 (world render modes; amended below it).

## Engine Compatibility

Godot 4.7.2 mono. Uses only the canvas shading language features the world
shaders already use, plus `TIME` and one extra `sampler2D` global uniform.
Works on every renderer the client exports with (Forward+, Mobile,
Compatibility for web); the realistic model must degrade on Compatibility and
WATER-04 says how: on Compatibility (`CURRENT_RENDERER == RENDERER_COMPATIBILITY`
in the shader preprocessor) it drops its finer chop and its sparkles, three and
one noise lookups a fragment, and keeps the rest.

## GDD Requirements Addressed

None: this is a client rendering option, not a game-design system. The
requirement is the owner's ask of 2026-10-18 (five styles, shard control).

## Stories this ADR governs

| Story | Delivers | Proof |
|---|---|---|
| WATER-02 | `SHADER_WATER`, the `uo` model built in, `Profile.WaterStyle`, Options entries, editor Layers choice, perf scenes | scenario `client.render.water`: Classic dumps at the docks and the bay equal `main`; style stills; perf table |
| WATER-03 | the ripple texture, mobiles, boats with wakes, falling items | scenario step walking into water and a boat moving; perf table |
| WATER-04 | `water` component in the Store; pixel, cel and realistic packs (hashed, signed) | corpus cases agree; one still per style |
| WATER-05 | Admin tab "Water rendering", server pack `water` block, client consent | scenario: lock and default honoured, decline keeps the player's choice |

## Validation

This ADR is validated when WATER-02 merges with the Classic parity dumps and
the perf table above. Numbers measured for this ADR: the cell counts in the
table, from the client data with `tools/guo` (`uomap`, `uoread.TileData`).

## Implementation notes (WATER-02)

- `SHADER_WATER` is a bit, not a mode value: bit 6 (0x40) of the mode byte,
  beside the mode (bits 0-5) and circle-of-transparency (bit 7), so a hued or
  lit Wet tile keeps its own mode. `docs/data_formats.md` section 44.
- The fragment finds its world pixel from one mapping the batcher sets per
  frame (`water_target_to_world`), and its cell from the mask's water height,
  so no vertex data was added.
- `Profile.WaterStyle` has no `PlatformDefaults` entry (decision 7).
- `--water-style ID` sets the style for one session without saving it, for
  probes, scenarios and the perf lab.
- Styles are `IWaterStyle` (`Id`, `DisplayName`, `Bind`); a pack style can
  implement it later whichever way decision 6 lands.
- The chunk mesh's per-frame hue sync (`GameSceneDrawingSorting.ApplyMeshHue`)
  rewrites each meshed sprite's mode, so it keeps the water bit too; without
  it deep-water land lost the style a frame after it was built.

Measured 2026-10-18 on the baseline machine (RTX 4090, 1280 x 720, a quiet
machine, one run each, so indicative): `britain-docks` mean frame 1.245 ->
1.355 ms (+0.11), `trinsic-bay` 1.095 -> 1.168 ms (+0.07), inside the 1 ms
budget. Draw calls rose at the bay (157 -> 199); WATER-03 looks at why. With
Classic, the docks and the bay are pixel-identical to `main` (0 of 921,600
pixels) with identical draw lists.

Ripples (WATER-03, 2026-10-19): a 20 Hz CPU wave field over a 128-cell
window (decision 5). The debug view (`GUO_WATER_DEBUG=2`) shows rings round a
wading character and a bow wave and wake behind a sailing boat. Perf with the
style on is within run-to-run noise of off at the docks and the bay (two runs
each, about +0.01 and 0.00 ms), and the bay's draw calls vary between runs
whether water is on or off (156 to 199), so the earlier 157 -> 199 was not the
water. Classic stays pixel-identical. Wave speed and stamps are per second,
not per frame.

## Implementation notes (WATER-04)

- The shader holds the four models (`water_model` 1 to 4); a style picks one.
  `uo` is unchanged by the addition: at grain 1 its pixel snap is the same.
- A `water` component's style file is `guo/water@1`
  (`docs/data_formats.md` section 44), checked whole when a pack is verified
  or installed, by `water_style.py` and `StoreWater.cs`; the corpus has 49
  water cases and both validators agree on all 183 files.
- The deployment's styles are offered after the built-in one, by their
  `name`, and saved as `pack-id:component-id`. They need no numeric binding.
- `guo-water-pixel`, `guo-water-cel` and `guo-water-realistic` are built from
  `design/packs/water` and published like any pack; the catalogue's index
  signature covers their hashes.
