#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using Godot;

internal readonly record struct LegendItem(string Label, Color Colour);

/// <summary>What a mode draws from: the view's geometry, the cell data, and the user's choices.</summary>
internal sealed class ModeContext
{
    public WorldHost Host;
    public WorldData Data;
    public CellGeometry Geo;

    /// <summary>Tinted: translucent over the world. Solid: opaque, hiding it.</summary>
    public bool Tinted = true;

    /// <summary>The cell Reachability fills from, and the fill.</summary>
    public (int X, int Y, sbyte Z)? Origin;
    public ReachFill Reach;

    /// <summary>The world project's changed blocks on this facet, for Project diff.</summary>
    public HashSet<long> ChangedBlocks = new();

    /// <summary>Per frame time budget for the heavy answers; null means unlimited (a scene pack).</summary>
    public System.Diagnostics.Stopwatch Clock;
    public double BudgetMs = 10;

    public bool OverBudget => Clock != null && Clock.Elapsed.TotalMilliseconds > BudgetMs;

    public float Alpha => Tinted ? 0.5f : 1f;

    public Color Tint(Color c) => new(c.R, c.G, c.B, Alpha);
}

/// <summary>
/// A diagnostic view of the world (ADR-0027): recolours the cells in view,
/// and says what the colours mean. Drawn through <see cref="IPaint"/>, so the
/// same code paints the World tab and a scene pack's image.
/// </summary>
internal interface IWorldMode
{
    string Name { get; }
    string Summary { get; }

    /// <summary>ED6: one plain sentence on what the colours mean, shown under the legend's title and on the View menu.</summary>
    string Meaning { get; }
    IReadOnlyList<LegendItem> Legend(ModeContext ctx);
    void Draw(IPaint paint, ModeContext ctx);

    /// <summary>The mode's word for a cell ("blocked", "wall", ...), for hover text and the smoke check.</summary>
    string Classify(ModeContext ctx, int x, int y);

    string Describe(ModeContext ctx, int x, int y);
}
#endif
