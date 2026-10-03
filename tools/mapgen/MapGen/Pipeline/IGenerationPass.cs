using CentrED.MapGen.IR;

namespace CentrED.MapGen.Pipeline;

/// <summary>
/// Contract for a single procedural map-generation pass. Params are JSON-serializable POCOs
/// (typed as <see cref="object"/>) so the reflection-based ImGui inspector can render
/// sliders/dropdowns from attributes on the POCO.
/// </summary>
public interface IGenerationPass
{
    /// <summary>Stable identifier used for reports, JSON keys, and pipeline gating.</summary>
    string Name { get; }

    /// <summary>UI grouping: "Terrain", "Climate", "Biome", "Hydrology", "Network", "Scatter", "Pois".</summary>
    string Category { get; }

    /// <summary>Dense IR fields this pass reads — validated by <see cref="PipelineRunner.Validate"/>.</summary>
    IrFields Reads { get; }

    /// <summary>Dense IR fields this pass writes — pre-allocated by the runner before <see cref="Run"/>.</summary>
    IrFields Writes { get; }

    /// <summary>Returns a fresh default parameters POCO. The runner deep-clones this for each run so user edits in the inspector don't mutate the default.</summary>
    object CreateDefaultParams();

    /// <summary>Executes the pass against the IR carried on <paramref name="ctx"/>.</summary>
    void Run(GenContext ctx, object parameters);
}
