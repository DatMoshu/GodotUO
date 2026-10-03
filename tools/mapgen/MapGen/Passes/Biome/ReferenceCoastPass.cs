using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class ReferenceCoastParams
{
    [TunableDisplay("Coast atlas", Tooltip = "Local atlas mined by handoff/analyzer/coast_atlas.py. Contains tile frequencies and source hashes; never modifies the reference map.")]
    public string AtlasPath { get; set; } = "Data/map-mining/felucca-coast-atlas.json";
    [TunableDisplay("Minimum reference support")]
    [TunableRange(1, 1000)]
    public int MinimumSupport { get; set; } = 30;
    [TunableDisplay("Reference foam", Tooltip = "Use measured foam sprites, including partial pieces on mixed dry/wet terrain corners.")]
    public bool ReferenceFoam { get; set; } = true;
}

// Natural shore art depends on the four terrain vertices, not just which cell
// is labelled grass. A locally mined atlas preserves observed orientations.
// The pass paints IDs only: the generator still owns geometry and water levels.
public sealed class ReferenceCoastPass : IGenerationPass
{
    public string Name => "Reference Coast";
    public string Category => "Biome";
    public IrFields Reads => IrFields.Height | IrFields.LandId | IrFields.Biome;
    public IrFields Writes => IrFields.LandId | IrFields.StaticOps;
    public object CreateDefaultParams() => new ReferenceCoastParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (ReferenceCoastParams)parameters;
        var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null || ir.Biome is null) return;
        string path = RepoRootResolver.Resolve(p.AtlasPath);
        if (!File.Exists(path))
        {
            ctx.Report.Warnings.Add("Reference Coast atlas missing; existing brush transitions retained. Mine the atlas from a local reference profile first.");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported coast atlas schema");
        var exact = Read(doc.RootElement.GetProperty("exact"));
        var corners = Read(doc.RootElement.GetProperty("corners"));
        var bedExact = doc.RootElement.TryGetProperty("bed_exact", out var be) ? Read(be) : exact;
        var bedCorners = doc.RootElement.TryGetProperty("bed_corners", out var bc) ? Read(bc) : corners;
        var foamExact = p.ReferenceFoam && doc.RootElement.TryGetProperty("foam_exact", out var fe) ? Read(fe) : null;
        var foamCorners = p.ReferenceFoam && doc.RootElement.TryGetProperty("foam_corners", out var fc) ? Read(fc) : null;
        var foamCells = new HashSet<int>();
        var bedFoamExact = doc.RootElement.TryGetProperty("bed_foam_exact", out var bfe) ? Read(bfe) : foamExact;
        var bedFoamCorners = doc.RootElement.TryGetProperty("bed_foam_corners", out var bfc) ? Read(bfc) : foamCorners;
        var foamOps = new List<StaticOp>();
        var depthRules = doc.RootElement.TryGetProperty("depth_exact", out var de) ? Read(de) : null;
        var scope = ir.Scope;
        int painted = 0, fallback = 0, unsupported = 0;
        bool Wet(int x, int y) => ir.Height_Z[ir.Index(x, y)] <= ir.OceanZ &&
            (BiomeId)ir.Biome[ir.Index(x, y)] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;
        int sw = scope.Width, sh = scope.Height;
        var dry = new bool[sw * sh];
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) dry[y * sw + x] = !Wet(scope.X1 + x, scope.Y1 + y);
        var depth = FieldOps.Distance4(dry, sw, sh, 6);
        int depthPainted = 0;
        for (int y = Math.Max(1, (int)scope.Y1); y <= Math.Min(ir.Height - 2, scope.Y2); y++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            for (int x = Math.Max(1, (int)scope.X1); x <= Math.Min(ir.Width - 2, scope.X2); x++)
            {
                int i = ir.Index(x, y);
                bool wet = Wet(x, y);
                bool bed = wet && ir.Height_Z[i] < ir.OceanZ;
                int lx = x - scope.X1, ly = y - scope.Y1;
                int ring = depth[ly * sw + lx];
                if (depthRules is not null && wet && !TileFlags.IsWaterLandId(ir.LandId[i]) && ring >= 2 && ring <= 4
                    && lx > 0 && ly > 0 && lx + 1 < sw && ly + 1 < sh)
                {
                    int dm = 0, db = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++, db++) if (depth[(ly + dy) * sw + lx + dx] >= ring) dm |= 1 << db;
                    if (depthRules.TryGetValue($"{ring}:{dm}", out var depthChoices) && depthChoices.Sum(c => c.Count) >= p.MinimumSupport)
                    {
                        uint depthRoll = FieldOps.Hash(x, y, ir.Seed ^ 0x4445505448UL) % (uint)depthChoices.Sum(c => c.Count);
                        foreach (var c in depthChoices)
                        {
                            if (depthRoll < c.Count) { ir.LandId[i] = c.Tile; depthPainted++; break; }
                            depthRoll -= (uint)c.Count;
                        }
                    }
                }
                // A bed tile needs a water surface above it. Narrow channels may
                // deliberately retain animated water land instead of a dug bed.
                if (!wet && (ir.Height_Z[i] < ir.SeaLevelZ - 2 || ir.Height_Z[i] > ir.SeaLevelZ + 3)) continue;
                int mask = 0, bit = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++, bit++)
                    if (Wet(x + dx, y + dy)) mask |= 1 << bit;
                if (mask == 0 || mask == 511) continue;
                if (foamExact is not null && foamCorners is not null)
                {
                    int four = ((mask >> 4) & 1) | (((mask >> 5) & 1) << 1) | (((mask >> 7) & 1) << 2) | (((mask >> 8) & 1) << 3);
                    var fExact = bed ? bedFoamExact! : foamExact;
                    var fCorners = bed ? bedFoamCorners! : foamCorners;
                    if (!fExact.TryGetValue($"1:{mask}", out var foamChoices) || foamChoices.Sum(c => c.Count) < p.MinimumSupport)
                        fCorners.TryGetValue($"1:{four}", out foamChoices);
                    if (foamChoices is not null && foamChoices.Sum(c => c.Count) >= p.MinimumSupport)
                    {
                        foamCells.Add(i);
                        uint foamRoll = FieldOps.Hash(x, y, ir.Seed ^ 0x464F414DUL) % (uint)foamChoices.Sum(c => c.Count);
                        foreach (var choice in foamChoices)
                        {
                            if (foamRoll < choice.Count)
                            {
                                if (choice.Tile != 0) foamOps.Add(new StaticOp(StaticOpKind.Add, (ushort)x, (ushort)y, (sbyte)ir.OceanZ, choice.Tile, 0));
                                break;
                            }
                            foamRoll -= (uint)choice.Count;
                        }
                    }
                }
                if (wet && TileFlags.IsWaterLandId(ir.LandId[i])) continue;
                int material = Material((BiomeId)ir.Biome[i]);
                if (wet)
                {
                    int best = int.MaxValue;
                    for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < scope.X1 || ny < scope.Y1 || nx > scope.X2 || ny > scope.Y2 || Wet(nx, ny)) continue;
                        int candidate = Material((BiomeId)ir.Biome[ir.Index(nx, ny)]);
                        int distance = dx * dx + dy * dy;
                        if (candidate != 0 && distance < best) { material = candidate; best = distance; }
                    }
                }
                if (material == 0) continue;
                var landExact = bed ? bedExact : exact;
                var landCorners = bed ? bedCorners : corners;
                if (!landExact.TryGetValue($"{material}:{mask}", out var choices) || choices.Sum(c => c.Count) < p.MinimumSupport)
                {
                    int four = ((mask >> 4) & 1) | (((mask >> 5) & 1) << 1) | (((mask >> 7) & 1) << 2) | (((mask >> 8) & 1) << 3);
                    if (!landCorners.TryGetValue($"{material}:{four}", out choices) || choices.Sum(c => c.Count) < p.MinimumSupport) { unsupported++; continue; }
                    fallback++;
                }
                int total = choices.Sum(c => c.Count);
                uint roll = FieldOps.Hash(x, y, ir.Seed ^ 0x434F415354UL) % (uint)total;
                foreach (var choice in choices)
                {
                    if (roll < choice.Count) { ir.LandId[i] = choice.Tile; painted++; break; }
                    roll -= (uint)choice.Count;
                }
            }
        }
        int removed = ir.StaticOps.RemoveAll(o => o.Kind == StaticOpKind.Add && o.Id >= 0x179D && o.Id <= 0x17B2 && foamCells.Contains(ir.Index(o.X, o.Y)));
        ir.StaticOps.AddRange(foamOps);
        // These sprites are shaped water surfaces, not a foam decal to put on
        // a full diamond. Full water beneath fills their transparent cutouts.
        var shaped = foamOps.Select(o => (o.X, o.Y, o.Z)).ToHashSet();
        int fullRemoved = ir.StaticOps.RemoveAll(o => o.Kind == StaticOpKind.Add &&
            o.Id >= 0x1797 && o.Id <= 0x179C && shaped.Contains((o.X, o.Y, o.Z)));
        ctx.Report.Notes.Add($"full water replaced by shaped shore surfaces={fullRemoved}");
        ctx.Report.StaticsAdded = foamOps.Count;
        ctx.Report.Notes.Add($"reference foam={foamOps.Count}, legacy foam replaced={removed}");
        ctx.Report.Notes.Add($"oriented offshore gradients={depthPainted}");
        ctx.Report.TilesTouched = painted;
        ctx.Report.Notes.Add($"reference shore tiles={painted}, four-corner fallbacks={fallback}, unsupported={unsupported}; atlas={p.AtlasPath}");
    }

    private static int Material(BiomeId b) => b switch
    {
        BiomeId.Grassland or BiomeId.Savanna => 1,
        BiomeId.Forest or BiomeId.DenseForest => 2,
        BiomeId.Jungle => 3,
        BiomeId.Beach or BiomeId.Desert => 4,
        _ => 0,
    };

    private static Dictionary<string, (ushort Tile, int Count)[]> Read(JsonElement root)
    {
        var result = new Dictionary<string, (ushort, int)[]>();
        foreach (var pair in root.EnumerateObject())
        {
            var values = pair.Value.EnumerateArray().Select(v => (Tile: v[0].GetInt32(), Count: v[1].GetInt32())).ToArray();
            if (values.Any(v => v.Tile < 0 || v.Tile >= 0x4000 || v.Count <= 0)) throw new InvalidDataException("Invalid coast atlas entry");
            // Rare observations can be repairs, buildings, or atypical banks.
            // Retain common variants, while excluding low-support outliers.
            int peak = values.Select(v => v.Count).DefaultIfEmpty(0).Max();
            result[pair.Name] = values.Where(v => v.Count >= Math.Max(3, peak / 5)).Select(v => ((ushort)v.Tile, v.Count)).ToArray();
        }
        return result;
    }
}
