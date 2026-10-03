namespace CentrED.MapGen.IR;

// Per-pass biome diagnostics — populated by BiomeStaticScatterPass + ImageImportPass,
// surfaced in MapGenWindow's "Biome Diagnostics" expandable section after a run.
//
// Aggregates per biome:
//  - CellsInScope    : total cells that carry this BiomeId in the scope
//  - Evaluated       : Poisson-disc samples that landed on this biome
//  - GroupsPlaced    : scatter groups actually placed
//  - TilesPlaced     : statics emitted (each group → N tiles)
//  - Rejected*       : reasons a candidate was thrown out
//  - EffectiveChance : final chance gate used (chance% × density)
//  - LandIdHistogram : per-LandId counts written by ImageImportPass (catches the
//                      "UNUSED 0x78" issue visible in image 35)
public sealed class BiomeDiagnostics
{
    public Dictionary<BiomeId, BiomeStats> ByBiome { get; } = new();

    public BiomeStats GetOrAdd(BiomeId b)
    {
        if (!ByBiome.TryGetValue(b, out var s))
            ByBiome[b] = s = new BiomeStats();
        return s;
    }
}

public sealed class BiomeStats
{
    public int CellsInScope;
    public int Evaluated;
    public int GroupsPlaced;
    public int TilesPlaced;

    public int RejectedSeaLevel;
    public int RejectedWater;
    public int RejectedSlope;
    public int RejectedSkipForest;
    public int RejectedNoCatalogue;
    public int RejectedChanceGate;
    public int RejectedEmptyGroup;

    public double EffectiveChance;

    // LandId histogram (ImageImportPass), keyed by ushort LandId → count. Sparse —
    // typically 4-7 entries per biome (the pool the palette assigns).
    public Dictionary<ushort, int> LandIdHistogram { get; } = new();
}
