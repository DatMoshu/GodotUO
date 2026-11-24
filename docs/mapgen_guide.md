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

## Start from painted terrain and heights

In **MapGen → Setup**, select **Use painted draft…** and choose
`NAME.terrain.png`. Put `NAME.altitude.png` beside it. Use two single-frame
PNGs of the same dimensions, with one pixel per map cell. Both sides must be
multiples of eight, at least 64 cells, and at most 7168 wide by 4096 high.
The upper-left pixel is (0, 0). Example terrain swatches include deep water
(`#000080`), grass (`#60C040`), forest (`#208020`) and mountain (`#808080`).
Paint the altitude image in grayscale. Black starts at Z −10 and white at Z 80.

Keep the seed fixed while adjusting **Coast Terrace** and **Forest Scatter**.
Later passes can change the painted elevations. Compare **Biomes**, **Height**
and **Radar** after generating. An unmatched terrain colour produces a warning
with its first coordinate. After export, **Inspect draft warning** opens that
cell in World. To revise the layout, repaint the original pair and generate
again. Each completed run keeps its own input copies for export.

## Tune with a fixed seed

In World, **Brush library → Terrain palette…** opens a locally supplied UO Landscaper
`Data/System/Terrain.xml`. Choose a named terrain to paint its displayed hexadecimal land ID.
The brush preserves ground elevation, size, density, spacing and footprint. A swatch supplies
one base tile and replaces the previous variant mix and edge rules. The pinned norad32 table
has 93 entries with decimal TileIDs from 2 through 16337. The picker reads the name and
base TileID, and checks the selected tile against the loaded client's art. RGB, terrain class,
randomization and altitude fields are outside this import. Use **Single** or **Scatter** in the same library to
place statics. Terrain palette import reads the base tiles only. Coast transitions, altitude
colour maps and static placement catalogues have separate formats.

To finish an existing shoreline, open **Terrain palette…** and choose the JSON value of a
CentrED# profile’s `land_brushes` setting. Select the directed pair in the swatch menu, unlock
terrain, and drag **Coast edges** over the source bank. The destination entry’s `Tiles` identify
the neighboring shore. The brush recognizes source base tiles and its own previously painted
edges. It preserves elevation and limits edits to the stroke. Density, footprint, allowed land,
water protection and slope filters still apply. Undo reverses one stroke. Saving a brush preset
includes the selected pair.

Direction bits run clockwise from north (1) through northwest (128). The smallest rule mask
containing all destination neighbors wins. Equal-size matches use the brush seed and map
coordinates. Cells without a matching rule keep their tile. A usable pair has disjoint source
and destination IDs and art available in the loaded client. For a CentrED+ `TilesBrush.xml`,
convert it in CentrED# first. Its pinned reader recognizes DR, DL, UL, UR, LL and UU and drops
base tile Chance. Inspect that conversion before painting. World rejects zero-direction rules
and unsupported JSON fields, including Chance.

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
