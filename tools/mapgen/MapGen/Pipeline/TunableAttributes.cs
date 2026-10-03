namespace CentrED.MapGen.Pipeline;

// Attributes consumed by the (forthcoming) reflection-based ImGui inspector.
// Defined in the core project so pass param POCOs don't need a UI dependency.

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class TunableRangeAttribute : Attribute
{
    public double Min { get; }
    public double Max { get; }
    public TunableRangeAttribute(double min, double max) { Min = min; Max = max; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class TunableDisplayAttribute : Attribute
{
    public string Label { get; }
    public string? Tooltip { get; init; }
    public TunableDisplayAttribute(string label) { Label = label; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class TunableTileSetAttribute : Attribute
{
    public string Category { get; }   // e.g. "land", "static"
    public TunableTileSetAttribute(string category) { Category = category; }
}
