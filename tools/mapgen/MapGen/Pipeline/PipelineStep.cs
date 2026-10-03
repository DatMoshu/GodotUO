namespace CentrED.MapGen.Pipeline;

// One slot in the pipeline: a pass + its current params + on/off toggle.
// The runner consumes a List<PipelineStep> in order.
public sealed class PipelineStep
{
    public required IGenerationPass Pass { get; init; }
    public required object Parameters { get; set; }
    public bool Enabled { get; set; } = true;
    // As-built default Enabled state (captured by DefaultPipeline.Build). Used by
    // MapGenPreset.ApplyTo to reset a step to its default rather than blanket-enabling
    // — without this, applying any preset silently turns on every DefaultDisabled pass
    // (e.g. "Maze Stamp", "POI Stamps") because no preset lists them in disable_passes.
    public bool DefaultEnabled { get; init; } = true;
}
