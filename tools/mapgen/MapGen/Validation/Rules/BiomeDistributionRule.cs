using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Distribution-shape regression net for the BiomeAssign cascade. Catches the failure
// modes we've already hit at least once each: all-Forest (saturated moisture sweep),
// all-Desert (cratered moisture at small scale), no-Grassland (cascade window closed).
//
// Rule is intentionally a *warning*, not an auto-fix — there's nothing it can fix; the
// preset / climate / cascade thresholds are upstream. The point is to fail loudly in
// the run report rather than have a designer notice "wait, where's the grass" weeks
// later. Bands are deliberately wide so legitimate exotic presets (jungle, barren)
// don't trip them; presets that *should* be lopsided pick another BiomeProfile.
public sealed class BiomeDistributionRule : IMapRule
{
    public string Name => "BiomeDistribution";
    public bool AutoFixable => false;
    public bool Enabled { get; set; } = true;

    // Per-biome bands as (min%, max%) of *land* tiles (water excluded from the denominator).
    // Wide ranges — these are sanity bounds, not balance bounds. The bands depend on the
    // world being built, so they come in named profiles chosen by the Map Validator's
    // BiomeProfile: "felucca" (the default, a Britannia-like mix), "desert" and "ice" for
    // planets that are meant to be lopsided, and "none" (histogram only).
    public sealed record Profile((BiomeId biome, double minPct, double maxPct)[] Bands, double SingleBiomeMaxPct);

    public static readonly IReadOnlyDictionary<string, Profile> Profiles =
        new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase)
        {
            ["felucca"] = new(new[]
            {
                (BiomeId.Grassland,    5.0, 85.0),
                (BiomeId.Forest,       0.0, 70.0),
                (BiomeId.Desert,       0.0, 60.0),
                (BiomeId.Mountain,     0.0, 40.0),
            }, 92.0),
            ["desert"] = new(new[]
            {
                (BiomeId.Desert,      50.0, 100.0),
                (BiomeId.Grassland,    0.0, 10.0),
                (BiomeId.Forest,       0.0, 5.0),
                (BiomeId.Mountain,     0.0, 40.0),
            }, 100.0),
            ["ice"] = new(new[]
            {
                (BiomeId.Snow,        30.0, 100.0),
                (BiomeId.Grassland,    0.0, 10.0),
                (BiomeId.Forest,       0.0, 10.0),
                (BiomeId.Mountain,     0.0, 40.0),
            }, 100.0),
            ["none"] = new(Array.Empty<(BiomeId, double, double)>(), 100.0),
        };

    public const string DefaultProfile = "felucca";

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Biome is null) return result;
        var profileName = string.IsNullOrWhiteSpace(ctx.BiomeProfile) ? DefaultProfile : ctx.BiomeProfile;
        if (!Profiles.TryGetValue(profileName, out var profile))
        {
            result.Add(new ValidationFinding(Name, ValidationSeverity.Error,
                $"unknown biome profile \"{profileName}\" (known: {string.Join(", ", Profiles.Keys)})"));
            return result;
        }

        var counts = new int[256];
        int landTotal = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            byte b = ir.Biome[ir.Index(x, y)];
            counts[b]++;
            // Exclude water + beach from the denominator: land-biome distribution is
            // what we actually care about. A 95%-ocean map shouldn't trip rules
            // because Grassland is "only" 0.5% of all tiles.
            if (!IsWaterOrBeach((BiomeId)b)) landTotal++;
        }

        if (landTotal == 0)
        {
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                "scope contains no land — biome distribution undefined"));
            return result;
        }

        // Per-biome band checks.
        foreach (var (biome, lo, hi) in profile.Bands)
        {
            double pct = 100.0 * counts[(int)biome] / landTotal;
            if (pct < lo)
                result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                    $"{biome} at {pct:F1}% of land — below the {profileName} profile's min {lo:F0}%"));
            else if (pct > hi)
                result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                    $"{biome} at {pct:F1}% of land — above the {profileName} profile's max {hi:F0}%"));
        }

        // Single-biome dominance check (any non-water/beach biome >SingleBiomeMaxPct).
        int dominantIdx = -1;
        double dominantPct = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            if (IsWaterOrBeach((BiomeId)i)) continue;
            double pct = 100.0 * counts[i] / landTotal;
            if (pct > dominantPct) { dominantPct = pct; dominantIdx = i; }
        }
        if (dominantIdx >= 0 && dominantPct > profile.SingleBiomeMaxPct)
        {
            result.Add(new ValidationFinding(Name, ValidationSeverity.Error,
                $"{(BiomeId)dominantIdx} dominates {dominantPct:F1}% of land — moisture cascade is likely broken"));
        }

        // Always emit one Info row with the full histogram so the run report carries
        // the distribution even when the run passes.
        var present = new List<string>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            var label = Enum.IsDefined(typeof(BiomeId), (byte)i) ? ((BiomeId)i).ToString() : $"#{i}";
            double pct = 100.0 * counts[i] / landTotal;
            present.Add($"{label}={pct:F1}%");
        }
        result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
            $"land-biome distribution ({profileName} profile): " + string.Join(", ", present)));

        return result;
    }

    private static bool IsWaterOrBeach(BiomeId b) =>
        b is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.Beach or BiomeId.River;
}
