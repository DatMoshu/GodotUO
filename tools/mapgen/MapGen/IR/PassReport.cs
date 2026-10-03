namespace CentrED.MapGen.IR;

public sealed class PassReport
{
    public required string PassName { get; init; }
    public TimeSpan Elapsed { get; set; }
    public int TilesTouched { get; set; }
    public int StaticsAdded { get; set; }
    public int StaticsRemoved { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();
    // Optional per-biome stats (BiomeStaticScatter, ImageImport). Null when the pass
    // doesn't produce biome-level data. Surfaced in MapGenWindow's diagnostic section.
    public BiomeDiagnostics? Diagnostics { get; set; }
}
