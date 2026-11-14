# MapGen workflow review

This review covers the Godot editor tab, the generator pipeline and the CLI's run and export contract. World and Maps are integration surfaces. The editor work is L3, and the CLI work is tooling. The acceptance map uses `felucca-stage18`, seed 1234567 and 256 × 256 tiles. The disposable test project contains the pending World workbench snapshot, including the original MapGen restore implementation.

## Findings, ordered by impact

| Priority | Reproduction and impact | Code and disposition |
|---|---|---|
| High | Start an export and interrupt it while writing. The original CLI opened the canonical `export/` directory before regenerating, so a partial write could block every retry of that run. | [ExportCommand and StagedFolder](../tools/mapgen/cli/Export.cs). Stage both outputs, publish after read-back and the World write, use a new staging path on retry. A killed process can leave disposable staging folders. |
| High | Open MapGen at 1024 × 768 with the Inspector dock visible. The original three columns pushed the map outside the window. At 1366 × 768 the map and its actions competed with diagnostics and the off-screen bottom of Setup. | [MapGenView layout](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). Setup and Passes share one pane. The preview gets the remaining width, actions stay outside the scroll regions, and diagnostics open on demand. |
| High | Point `UO_PYTHON` at a missing executable, or disable Noise Height with Hydraulic Erosion enabled. Process startup could fault the UI task without clearing Busy. A pipeline could also start work before discovering an invalid later dependency. | [MapGenCli.RunAsync](../godot/GUO/addons/guo_editor/MapGen/MapGenCli.cs) and [Pipeline.Build](../tools/mapgen/cli/Run.cs). Report process launch failures and validate the full dense-field chain before generation. A failed attempt leaves the previous completed result restorable. |
| High | Use an explicit custom table path, then remove or invalidate the file. The loader could fall back to an empty table and produce unexpected hard biome boundaries. Cancel the file chooser after selecting GUO core and the original picker could also change the selection. | [Pipeline.Run](../tools/mapgen/cli/Run.cs), [LandBrushTable](../tools/mapgen/MapGen/Data/LandBrushTable.cs) and [MapGenView.PickTableFile](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). Explicit tables must load. Cancelling preserves the current selection. Default GUO retains its configured measured/core selection. |
| High | Compare a completion hash with `export-verify.json`, then click the preview's edge on a size that is unevenly reduced, such as 7168 × 4096 at a 1024-pixel preview cap. The old PNG reducer could drop trailing cells, the tab hardcoded facet 0 for clicks, and the tour compared only the two export receipts. | [MapImage.Save](../tools/mapgen/cli/Render.cs), [MulVerify](../tools/mapgen/cli/Export.cs), [MapGenView.OnPreviewInput](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs) and [MapGenResultSeg](../godot/GUO/addons/guo_editor/EditorTourWorld.cs). Preserve the full preview extent, use the published facet and origin, compare all three receipts, and verify the actual files against per-file digests. |
| High | Enable Map Validator's optional report and point it outside the configured MapGen data area. The arbitrary path bypassed the CLI's run and export directory check. | [MapValidatorPass](../tools/mapgen/MapGen/Passes/Validation/MapValidatorPass.cs) and [Pipeline.Build](../tools/mapgen/cli/Run.cs). Constrain report paths to MapGen data and reject paths in the configured client directory. Filesystem alias handling is a separate follow-up. |
| Medium | Load a preset with a negative or large seed, then switch to a seedless preset. The old SpinBox clamped to a nonnegative 32-bit range and the new setup could inherit the previous seed. Saving a name twice silently replaced the prior preset. | [MapGenView.LoadSchema, GenerateAsync and SavePreset](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). Use exact signed 64-bit text, use the CLI fallback for seedless presets, and reject duplicate names. Presets save pass settings and seed. Size and transition choice remain in the run manifest. |
| Medium | Change Setup after A completes, start and cancel B, then restore A. The two sets of inputs can differ. Drag to an early pass and the old layer picker still described the final Radar image. | [MapGenView result and preview methods](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). The result carries its own identity, restored warnings and steps. The layer picker applies to the final map, and the final map is an explicit step and the only preview that hands off. |
| Medium | Tune a large map and watch a long pass or late export. The old status announced finished passes as if they were running, showed a single warning per pass, and stopped providing useful feedback during writes. | [RunCommand](../tools/mapgen/cli/Run.cs), [ExportCommand](../tools/mapgen/cli/Export.cs) and [MapGenView progress and details](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). Add current pass and phase events, show multiple diagnostics and keep the likeness table out of the preview's default layout. Pass event diagnostics are still a bounded sample. |
| Medium | Disable Town Sites or other POI sources and leave road passes on, or disable Slope Re-derive while leaving Slope Derive on. Empty sparse inputs and earlier dense inputs are legal, so a pass can run without useful POIs or with slope data from before terrain reshaping. | [DefaultPipeline.Build](../tools/mapgen/MapGen/DefaultPipeline.cs), [PipelineRunner.Validate](../tools/mapgen/MapGen/Pipeline/PipelineRunner.cs) and [RoadGraphPass](../tools/mapgen/MapGen/Passes/Network/RoadGraphPass.cs). Ordered pass names and field hints now expose the pipeline. Add provenance and producer-specific checks before offering reorder controls. |
| Medium | Disable Dig Shore and look for Auto Coast's shore-depth layer. Its depth setting is suppressed while building defaults, before the preset and CLI toggles are applied. It does not automatically follow a later edit to the Dig Shore switch. | [DefaultPipeline.GateAutoCoastShoreDepth](../tools/mapgen/MapGen/DefaultPipeline.cs) and [PipelineFactory.BuildSteps](../tools/mapgen/MapGen/Pipeline/PipelineFactory.cs). Reconcile the two pass settings as a deliberate, versioned terrain change with before-and-after shoreline fixtures. |
| Medium | Try a full alternate-facet size or a custom rectangular map from the tab. The CLI accepts multiples of eight within its limits. The tab offers a fixed list and exports on facet 0 at (0, 0), including sizes associated with other facets. | [SchemaCommand.Sizes](../tools/mapgen/cli/Schema.cs), [MapGenView.FillSizes and ExportAsync](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs) and [WorldProjectWriter](../tools/mapgen/cli/WorldProjectWriter.cs). Documented in the guide. Add explicit destination and bounds validation before exposing other facet/origin choices. |
| Medium | Tune River Carve on 256 and move the same settings to 1024. With area scaling, identical source count values produce different numbers of candidates. Other settings mix tiles, Z, fractions, percentages and costs. Dragging a quick slider starts from the preset and can replace unlocked manual edits. | [RiverCarvePass](../tools/mapgen/MapGen/Passes/Hydrology/RiverCarvePass.cs), [TunableAttributes](../tools/mapgen/MapGen/Pipeline/TunableAttributes.cs) and [MapGenView.ApplyKnob](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). Settings show preset value, range and existing help. Follow up with explicit unit metadata and effective values, plus a custom state for quick sliders. |
| Medium | Enable Fast draft and compare the effective run's enabled passes with the checkboxes. Heavy preset-enabled passes may still look enabled in Setup while `--fast` skips them. | [PipelineFactory.HeavyPassNames](../tools/mapgen/MapGen/Pipeline/PipelineFactory.cs) and [MapGenView.BuildRunArgs](../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs). The result carries its draft mode. Show the effective skip on each pass row in a follow-up. |
| Medium | Export a map with validator diagnostics. Exact MUL read-back can pass while a map still has awkward slopes, missing road links or biome errors. Likeness measures a small set of distributions. | [MapValidatorPass](../tools/mapgen/MapGen/Passes/Validation/MapValidatorPass.cs) and [Likeness](../tools/mapgen/cli/Likeness.cs). Distinguish verified file read-back from terrain review. Add typed severity, locations and findings that can be opened in World. |
| Low | Switch to Biomes on a map with rivers or dungeon walls. River and CaveWall use the same black fallback as Unassigned. The layer has no legend. | [Render.BiomeColor](../tools/mapgen/cli/Render.cs) and [BiomeId](../tools/mapgen/MapGen/IR/BiomeId.cs). Assign distinct colours, label them and provide a selectable slope layer in the diagnostics follow-up. |

## What transfers from other tools

- **Dragon.** Ryandor's author-written DragonMod tutorial starts with the overall layout and uses indexed colours to encode terrain and altitude. It treats mountains, rivers, grass cliffs, roads and random statics as deliberate authoring stages. GUO can offer a painted land/water intent mask and separate local control for coast types. Transition and scatter rules can still resolve the exact tiles and objects. [Ryandor's Map Making Tutorial](https://ryandor.com/files/makingmaps.11.pdf)
- **UO Landscaper.** The archived Orbsydia release compiles painted BMPs. The mod author's documentation describes terrain-pair transitions, high and rough coast support, and terrain-specific static groups. GUO already has transition and scatter catalogues. Add a coverage view for missing pair and edge shapes, then preview beach, high coast and cliff treatments on the same shoreline. Use data supplied by the builder or GUO's own tables. [Orbsydia release archive](https://uo.wzk.cz/uolandscaper/) · [UO Landscaper Mod](https://github.com/norad32/uo-landscaper-mod)
- **MapCreator.** Its author documents synchronized indexed terrain and altitude images, with the palette as part of the authoring workflow. A GUO guided import could validate size, palette and signed Z, show the two registered images, then expose where transition tiles replace the painted intent. The generator library already has paint infrastructure. [MapCreator source and author README](https://github.com/rfbwhite/MapCreator-Development)
- **Houdini heightfields.** SideFX documents independent height and mask layers, feature masks from slope and elevation, erosion with flow diagnostics, and mask-driven scatter. For GUO, show flow accumulation, slope in Z per tile, biome ownership and eligible scatter cells as distinct overlays. Then add reproducible local masks for roughness and density. [Heightfield workflow](https://www.sidefx.com/docs/houdini/model/heightfields) · [Erosion](https://www.sidefx.com/docs/houdini/heightfields/erosion)
- **Unreal Landscape Splines.** Epic's editable control points, width, falloff and terrain deformation make long features steerable. GUO can use quantized waypoints and graded corridors for routes to town gates, with constraints for water crossings and passable slopes. A river analogue would constrain sources and outlets while the flow pass supplies downstream detail. [Epic's spline documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/landscape-splines-in-unreal-engine)

On the 1024 × 1024 reference run, the main coast has bays, spits and islands. Straight cuts still
appear at the west and east margins. River bends are visible across the map, with several long
reaches following mountain feet. The centre has a broad bright mass in Height, while thinner
northern ridges show more relief. The northern road network includes conspicuous horizontal runs
and square corners. Radar shows an almost continuous pattern of small statics across the interior.
The biome image exposes denser forest patches behind that texture. In World, the 256 × 256
handoff at (37, 59) shows individual trees, water statics and the shore slope. Use both scales
when judging whether the tile transitions and scatter groups read well.

## Follow-up plan

1. **Make validation actionable.** Preserve structured severity and cell or region locations in `run.json`. Add slope, water connectivity and road reachability overlays. Show missing optional data and the actual fallback before starting. Give the full report a stable location under MapGen data.
2. **Make a setup portable.** Save a recipe with dimensions, data fingerprints and the selected transition table. Add rectangular sizes, explicit destination facet and origin, and a World bounds check. Use canonical paths when checking output against the client install, including reparse points. Test a nonzero origin on an alternate facet.
3. **Give builders local control.** Expose paired height and terrain import through the existing paint library. Add separate intent masks for coast treatment, biomes and protected ground. Validate fixed corridor points and town gates before adding river and road waypoints. Keep procedural output reproducible from the recipe.
4. **Improve terrain by measured cases.** Reuse fixed seeds at small, medium and full facet sizes. For coasts, measure unresolved edge families and waterline continuity. For rivers, measure drainage connectivity, bank grade and long straight segments. For slopes, measure passability and broad plateaus. For roads, measure detour, switchbacks and reachability. For scatter, measure occupancy, group integrity and biome density. Compare Radar and World at matching cells before promoting a change to a preset.
5. **Reduce preview cost where it matters.** Use measured generation and PNG costs to evaluate selected checkpoints or cached intermediate layers. Keep deterministic RNG indexing and compare full map hashes for any pipeline scheduling change.

## Measurements

Three warm CLI trials per configuration and revision, alternating baseline and updated order.
Both used `felucca-stage18`, seed 1234567, GUO core, the same local radar data and an empty
optional-data folder. The engine column includes final file writes. Wall time also includes
process startup. Times are medians in milliseconds.

| Size and pass images | Before engine / wall | After engine / wall | After pass image time |
|---|---:|---:|---:|
| 256 × 256, off | 533 / 662 | 529 / 649 | 0 |
| 256 × 256, on | 619 / 733 | 619 / 738 | 82 |
| 1024 × 1024, off | 3700 / 3848 | 3680 / 3807 | 0 |
| 1024 × 1024, on | 4669 / 4790 | 4717 / 4844 | 947 |

All trials at each size shared one full map hash across revisions and preview settings
(`015a212d4712…` and `0736a676ada4…` respectively). The timing difference between revisions
does not establish a speedup. UI measurements cover synchronous pass image switching and
the tab's first schema load, which can include the cold CLI build. They do not measure input
latency through OS events or frame pacing at a full facet size.

## Verification

- The real editor tour passed at 1920 × 1080, 1366 × 768 and 1024 × 768. Each run generated A, changed
  the seed, size and river settings for B, cancelled B, restored A, exported A, and clicked the final
  preview at (37, 59). World reported facet 0 and the exact cell. The tour compared the completed
  run, export and World hashes and rehashed the three MUL files. It also exercised invalid pass
  dependencies, a missing Python executable and retry with the edited seed and river setting.
- The 256 × 256 generation action took 1.37–1.43 s in the baseline editor captures and
  1.27–1.55 s in the updated captures. Selecting a saved 256 × 256 pass took about 0.9 ms
  synchronously. Cold schema loads measured 4.2–6.1 s with fresh CLI output folders.
- 277 generator unit tests and the full standalone headless editor smoke passed. The MapGen smoke
  verified 65,536 land cells, 13,714 statics and 1,024 World blocks with zero read-back mismatches.
  It also checked deterministic runs, an effective pass toggle, rejection of a missing table and
  retry after a failed export. All 22 shipped presets matched the baseline at 256 × 256 in Fast draft.
- A direct CLI probe killed export during the World write, retried the same run and verified the
  three MUL digests. An 80 × 64 reduction probe checked every pixel in all three preview layers
  against the full-resolution source. An out-of-folder validator report was refused.
- The separate hot-reload probe passed its first smoke pass and Godot then failed to unload the
  assembly. The cause was not isolated. The repository-wide docs linter also reported the existing
  local `tools/uomapgen` directory with no README. `git diff --check` passed.
