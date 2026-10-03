using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Passes.Climate;
using CentrED.MapGen.Passes.Hydrology;
using CentrED.MapGen.Passes.Network;
using CentrED.MapGen.Passes.Pois;
using CentrED.MapGen.Passes.Rooms;
using CentrED.MapGen.Passes.Scatter;
using CentrED.MapGen.Passes.Stamp;
using CentrED.MapGen.Passes.Terrain;
using CentrED.MapGen.Passes.Validation;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen;

public static class DefaultPipeline
{
    // Passes that produce broken output today and are disabled by default. Users can
    // re-enable them in the UI after the underlying issues (no stamps in library,
    // building tile bleed-through) are fixed. Buildings should come from the blueprint
    // / multi system, not stamp-painted; until that lands, leave these off.
    private static readonly HashSet<string> DefaultDisabled = new()
    {
        "POI Stamps",
        "Road Stamps",
        "Maze Stamp",
        "Rooms Carve",
        "Rooms Stamp",
        "Dungeon Decor Scatter",
        "Dungeon Spawner Emit",
        // Town network is opt-in via inland-lakes-with-roads.preset.json. Default-off
        // so existing presets (which never mention these passes) don't suddenly grow
        // towns + roads when this code lands.
        "Town Sites",
        "Town Roads",
        // Library stamps change every preset's look, so they are opt-in like Road Stamps.
        "Stamp Scatter",
        "Town Stamps",
        "Reference Coast",
        "Swamp Surface",
    };

    // Canonical pass order. UI lets users toggle individual steps; reordering within a category
    // is allowed but cross-category moves should be discouraged because the runner's Reads/Writes
    // validator will refuse invalid topologies.
    public static List<PipelineStep> Build()
    {
        return new List<IGenerationPass>
        {
            new NoiseHeightPass(),
            new HydraulicErosionPass(),
            new SlopeDerivePass(),
            new MoistureClimatePass(),
            new BiomeAssignPass(),                 // classifies, then rebases Z so sea level = GenIR.SeaLevelZ = 0
            new MountainShapePass(),               // raises the Mountain biome into ranges (needs Biome, so after Biome Assign)
            new BiomeAltitudeJitterPass(),         // F-10: per-biome altitude jitter
            new RiverCarvePass(),                  // rivers/lakes as flat water bodies with stepped banks; before tiles are painted
            new LandIdResolvePass(),
            new CoastSmoothPass(),
            new AutoCoastPass(),
            new DigShorePass(),                    // brown dug bottom + water statics along open-sea shores (Felucca pattern)
            new CoastTerracePass(),                // F-8: shore rises inland at a bounded slope
            new LandTransitionPass(),              // brush-table edge tiles between biome classes
            new SwampSurfacePass(),
            new ReferenceCoastPass(),
            new TownSiteFinderPass(),              // opt-in (DefaultDisabled). Emits Town PoiStamps with Footprint+Gates; independent of PoiStampPass.
            new PoiStampPass(),                    // disabled by default — see DefaultDisabled
            new TownRoadPass(),                    // opt-in (DefaultDisabled). Paints intra-town cobble cross; appends RoadSegments for RoadCenterline.
            new RoadGraphPass(),
            new RoadCenterlinePass(),              // F-5: paint dirt/cobble + Z profile + edge tiles
            new RoadStampPass(),                   // disabled by default — see DefaultDisabled
            new MountainTrailPass(),               // "Mountain Path": trail over each range's lowest saddle (replaces the Scatter sine trench)
            new CliffEdgePass(),                   // cliff faces 0xDC-0xDF lining dirt inside rock; after roads and trails exist
            new SlopeDerivePass("Slope Re-derive"), // slope again after coast/rivers/roads reshaped Z, for the scatter passes
            new ForestScatterPass(),
            new BiomeStaticScatterPass(),          // F-9: covers non-forest biomes
            new MountainEdgeStaticsPass(),         // Cliff-edge boulders and optional interior stones
            new CaveWallStaticsPass(),             // Cave-wall statics 0x0241-0x0243 on CaveWall cells + stalagmite decor on Cave cells
            new MazeStampPass(),                   // Recursive-backtracker maze of stone-wall + marble-floor statics (disabled by default; opt in via maze.preset.json)
            new RoomsCarvePass(),                  // Rogue rooms+tunnels carver; stamps GenIR.DungeonZone (disabled by default; opt in via rooms-maze.preset.json)
            new RoomsStampPass(),                  // Reads DungeonZone, emits wall/floor StaticOps (disabled by default)
            new DungeonDecorScatterPass(),         // Reads DungeonZone, scatters bones/torches/etc. per room kind (disabled by default)
            new DungeonSpawnerEmitPass(),          // Writes Data/Spawns/rooms-maze-<seed>.json for ModernUO [ImportSpawners (disabled by default)
            new StampScatterPass(),                // library stamps by biome (disabled by default); before the validator so its fixes apply
            new TownStampPass(),                   // library town stamps inside Town footprints (disabled by default)
            new MapValidatorPass(),
            new StaticResnapPass(),                // last: moves occupancy-recorded statics by the validator's land-Z changes, drops duplicates
        }
        .Select(p => new PipelineStep
        {
            Pass = p,
            Parameters = p.CreateDefaultParams(),
            Enabled = !DefaultDisabled.Contains(p.Name),
            DefaultEnabled = !DefaultDisabled.Contains(p.Name),
        })
        .ToList()
        .Pipe(GateAutoCoastShoreDepth);
    }

    // When DigShorePass is enabled, suppress AutoCoastPass.DrawShoreDepth so we don't
    // double-apply the depth shift (DigShore drives both LandId and Z; AutoCoast's
    // ShoreDepthZDrop would just nudge the same cells once more, fighting our ring Zs).
    // The suppression intent is recorded on DigShoreParams.SuppressAutoShoreDepth so the
    // decision is co-located with the pass that owns the Z field, rather than being an
    // invisible mutation of AutoCoastParams from outside.
    private static List<PipelineStep> GateAutoCoastShoreDepth(List<PipelineStep> steps)
    {
        var dig = steps.FirstOrDefault(s => s.Pass is DigShorePass);
        if (dig is null) return steps;
        var digP = (DigShoreParams)dig.Parameters;
        if (!dig.Enabled || !digP.Enabled) return steps;

        var auto = steps.FirstOrDefault(s => s.Pass is AutoCoastPass);
        if (auto is null) return steps;
        var autoP = (AutoCoastParams)auto.Parameters;

        // Record the suppression on DigShoreParams (source of truth) then sync AutoCoast.
        digP.SuppressAutoShoreDepth = true;
        autoP.DrawShoreDepth = !digP.SuppressAutoShoreDepth;
        return steps;
    }
}

internal static class PipelineExtensions
{
    public static T Pipe<T>(this T value, Func<T, T> fn) => fn(value);
}
