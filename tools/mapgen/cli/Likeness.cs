using CentrED.MapGen.IR;

namespace GuoMapGen;

/// <summary>
/// The "Felucca-likeness" card: a few shares measured on the generated land, next to the same
/// shares measured on Felucca's main continent (the handoff's A/B table). It is a quick guide
/// for the UI, not the judge: the analyzer tools measure the full set.
/// </summary>
public static class Likeness
{
    // Felucca's surface (map 0, 5120x4096), measured 2026-10-03 by this card's own rules: the seabed is
    // water, as below. Classes come from the tile tables, so they line up with the analyzer's only roughly.
    public static readonly Dictionary<string, double> Felucca = new()
    {
        ["z0_share"] = 0.628,
        ["forest"] = 0.360,
        ["grass"] = 0.264,
        ["sand"] = 0.013,
        ["rock"] = 0.109,
        ["jungle"] = 0.064,
        ["shore_sand"] = 0.830,
    };

    /// <summary>
    /// Felucca's shallows: land tiles (light ring 0x4C-0x57, mid ring 0x58-0x63, flat bed 0x64) dug 10 below
    /// the water statics. The card counts them as water, so the coast it measures is the dry shore.
    /// </summary>
    public static bool IsSeabed(ushort id) => id is >= 0x4C and <= 0x64;

    /// <summary>Shore sand: the beach pool or the sand edge tiles (0x1A-0x4B: rippled sand and sand against grass).</summary>
    public static bool IsShoreSand(ushort id, HashSet<ushort> beach) => beach.Contains(id) || id is >= 0x1A and <= 0x4B;

    public static Dictionary<string, object?> Measure(GenIR ir)
    {
        var land = ir.LandId; var z = ir.Height_Z;
        if (land is null || z is null) return new() { ["available"] = false };

        var classOf = new Dictionary<ushort, string>();
        foreach (var (biome, ids) in ir.Tables.Land)
        {
            string? cls = biome switch
            {
                BiomeId.Forest or BiomeId.DenseForest => "forest",
                BiomeId.Grassland or BiomeId.Savanna => "grass",
                BiomeId.Beach or BiomeId.Desert => "sand",
                BiomeId.Mountain or BiomeId.HighMountain => "rock",
                BiomeId.Jungle => "jungle",
                BiomeId.Snow or BiomeId.Tundra => "snow",
                BiomeId.Swamp or BiomeId.Wetland => "swamp",
                _ => null,
            };
            if (cls is null) continue;
            foreach (var id in ids) classOf.TryAdd(id, cls);
        }
        foreach (var id in ir.Tables.Beach) classOf.TryAdd(id, "sand");

        var water = new HashSet<ushort>(ir.Tables.Water.Concat(ir.Tables.River)) { 0xA8, 0xA9, 0xAA, 0xAB, 0x136, 0x137 };
        int w = ir.Width, h = ir.Height, n = w * h;
        var beach = new HashSet<ushort>(ir.Tables.Beach);
        bool IsWater(int i) => water.Contains(land[i]) || IsSeabed(land[i])
            || (ir.Biome is { } b && (b[i] == (byte)BiomeId.DeepWater || b[i] == (byte)BiomeId.ShallowWater));

        var counts = new Dictionary<string, int>();
        int landCells = 0, z0 = 0, shore = 0, shoreSand = 0;
        for (int i = 0; i < n; i++)
        {
            if (IsWater(i)) continue;
            landCells++;
            if (z[i] == 0) z0++;
            string cls = classOf.GetValueOrDefault(land[i], "edge");
            counts[cls] = counts.GetValueOrDefault(cls) + 1;

            int x = i % w, y = i / w;
            bool coast = false;
            for (int dy = -1; dy <= 1 && !coast; dy++)
            for (int dx = -1; dx <= 1 && !coast; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx | dy) != 0 && nx >= 0 && ny >= 0 && nx < w && ny < h && IsWater(ny * w + nx)) coast = true;
            }
            if (coast) { shore++; if (IsShoreSand(land[i], beach)) shoreSand++; }
        }

        double Share(string c) => landCells == 0 ? 0 : counts.GetValueOrDefault(c) / (double)landCells;
        int statics = ir.StaticOps.Count(o => o.Kind == StaticOpKind.Add);
        var measured = new Dictionary<string, double>
        {
            ["z0_share"] = landCells == 0 ? 0 : z0 / (double)landCells,
            ["forest"] = Share("forest"), ["grass"] = Share("grass"), ["sand"] = Share("sand"),
            ["rock"] = Share("rock"), ["jungle"] = Share("jungle"),
            ["shore_sand"] = shore == 0 ? 0 : shoreSand / (double)shore,
            ["statics_per_100_land"] = landCells == 0 ? 0 : statics * 100.0 / landCells,
        };

        // Score: 100 minus the mean relative miss over the measures Felucca has a number for, floored at 0.
        double miss = measured.Where(kv => Felucca.ContainsKey(kv.Key)).Average(kv => Math.Min(1.0, Math.Abs(kv.Value - Felucca[kv.Key]) / Math.Max(Felucca[kv.Key], 0.05)));
        return new Dictionary<string, object?>
        {
            ["available"] = true,
            ["land_share"] = n == 0 ? 0 : landCells / (double)n,
            ["measured"] = measured.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
            ["felucca"] = Felucca,
            ["classes"] = counts.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / (double)Math.Max(1, landCells), 4)),
            ["score"] = Math.Round(Math.Max(0, 100 * (1 - miss)), 1),
        };
    }
}
