using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Stamp;

// Shared plumbing for the stamp passes (Stamp Scatter, Road Stamps, Town Stamps).
// Loading and placement live in CentrED.MapGen.Stamps (StampLoader / StampPlacer) so the
// editor, StampCompose and MapGenDump use exactly the same code; this file only holds
// the per-pass glue: opening the library with diagnostics, and finishing a pass.
internal static class StampPassSupport
{
    /// <summary>Opens the shared library and reports stale-index / corrupt-file diagnostics on the pass report.</summary>
    public static StampLibrary OpenLibrary(GenContext ctx, string stampsRoot)
    {
        var lib = StampLoader.Load(stampsRoot);
        foreach (var w in lib.Warnings) ctx.Report.Warnings.Add(w);
        return lib;
    }

    /// <summary>Reports corrupt files met while parsing, flushes queued scatter clears, re-blends seams, fills the report counts.</summary>
    public static void Finish(GenContext ctx, StampLibrary lib, int opsBefore, int corruptBefore)
    {
        var errors = lib.Errors;
        if (errors.Count > corruptBefore)
        {
            ctx.Report.Warnings.Add($"{errors.Count - corruptBefore} corrupt stamp files skipped (first: {errors[corruptBefore]})");
        }
        int added = ctx.IR.StaticOps.Count - opsBefore;
        int removed = StampPlacer.FinishPass(ctx.IR, n => ctx.Report.Notes.Add(n));
        ctx.Report.StaticsAdded = Math.Max(0, added);
        ctx.Report.StaticsRemoved = removed;
    }

    /// <summary>Summary like "placed=12 rejected=40 (slope=20, overlap=15, water=5)".</summary>
    public static string Summary(int placed, Dictionary<string, int> rejects)
    {
        int total = rejects.Values.Sum();
        if (total == 0) return $"placed={placed} rejected=0";
        var parts = rejects.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return $"placed={placed} rejected={total} ({string.Join(", ", parts)})";
    }

    public static void CountReject(Dictionary<string, int> rejects, string? reason)
    {
        string r = reason ?? "unknown";
        rejects[r] = rejects.TryGetValue(r, out var c) ? c + 1 : 1;
    }
}
