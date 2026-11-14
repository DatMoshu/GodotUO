# Build a map in MapGen

1. Open GUO with `launchers/editor/open_project.bat` and select **MapGen**. In **Setup**, choose
   a preset, use **256 × 256** for a trial, and keep the seed so you can compare changes. **Roll**
   picks another seed. **GUO core** uses the committed transition table. **GUO** uses your measured
   transition weights when available.
2. Select **Generate**. With **Save pass previews** on, images appear as passes finish. The
   finished map has its own preset, seed, dimensions and short hash. Hover the identity for the
   full hash and run folder. **Fast draft** skips heavy passes. Turn it off and generate again
   when you want the full preset.
3. Drag the **Pass** slider left to inspect intermediate stages. Use **Final map** to return to
   **Radar**, **Biomes** or **Height**. **Run details** shows warnings and pass timings alongside
   a separate Felucca comparison. Radar uses biome colours when client radar data is unavailable.
4. Select **Export map files** on the result you want. MapGen regenerates it, compares the map hash,
   reads back the three MUL files, and writes a World project. **Verified export** confirms that
   read-back. Review any terrain warnings separately.
5. Select **Open in UO World** for the map centre or click a cell on the **Final map** for that
   coordinate. The tab exports on facet 0 at origin (0, 0). Continue manual terrain and static
   edits in World. Deploying to a shard is a separate workflow.

## Tune with a fixed seed

Start with **Shape terrain** in Setup. In **Passes**, search for a pass, setting or category.
Click its numbered row to expand settings and use the checkbox to enable it. The row tooltip
lists the pass's input and output fields. Setting tooltips show the preset value and range.
**Lock** protects a value from Randomise and the terrain sliders. **Reset** restores that pass
to the loaded preset.

| Inspect | Useful first controls |
|---|---|
| Coast silhouette and bays | **Noise Height** shape and strength, then **Coast Smooth** and **Coast Terrace**. Inspect the early Height image and the finished Radar waterline. |
| Rivers and slopes | **River Carve** source heights, source count and meander frequency. Source count scales with area when enabled. Terrace rise is in Z units per tile. |
| Biome boundaries | **Biome Assign**, then **Land Transitions**. Compare Biomes with Radar to distinguish a classification boundary from its tile treatment. |
| Roads and scatter | **Road Graph** clearance and slope costs, then **Road Centerline**. Use **Forest Scatter** and **Biome Static Scatter** to tune vegetation spacing and density. |

If a run fails or you cancel it, the attempt cannot be exported. **Show previous completed run**
returns to the prior result. Setup and Passes still hold your edits for the next attempt. Fix the
reported setting or dependency and select **Generate** to retry. If the generator cannot start,
check Python 3.12, the .NET SDK and `UO_PYTHON`. A missing custom transition table needs a new file
selection or a switch back to GUO. The built-in GUO tables work without importing Dragon or UO
Landscaper data.

Runs go under `UO_BUILD_DIR/mapgen/runs`. A completed run has `run.json` and its effective
`preset.json`. The export has `export/export-verify.json` with the run hash and a SHA-256 digest
for each file. The World project's `generated.json` records that same map hash, facet and origin.
**Save completed settings** saves the selected run's passes and seed as a reusable preset. Keep
`run.json` for its dimensions and transition-table choice.
