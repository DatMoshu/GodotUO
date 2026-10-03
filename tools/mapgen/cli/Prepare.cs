using CentrED.MapGen.Data;
using CentrED.MapMiner.Mining;

namespace GuoMapGen;

/// <summary>
/// <c>prepare --dragon DIR</c>: builds the land brush table from the user's own copy of the community
/// map tool Dragon (its <c>Scripts/map/*.txt</c> transition rules) into the generator data folder,
/// <c>UO_MAPGEN_DATA/landbrush.dragon.json</c>. GUO does not ship that table (docs/upstream/mapgen.md).
/// Without it, Land Transitions leaves biome borders as hard edges.
/// </summary>
public static class PrepareCommand
{
    public static int Execute(Args a, JsonEmitter json)
    {
        string dragon = a.Get("dragon") ?? throw new CliError("prepare needs --dragon DIR: a Dragon install, or its Scripts/map folder");
        string rules = FindRules(dragon) ?? throw new CliError($"no Dragon transition rules (Scripts/map/*2*.txt) under {dragon}");
        string output = a.Get("out") ?? RepoRootResolver.Resolve(LandBrushTable.DataJsonRelativePath);

        var report = DragonRulesImporter.Run(new DragonRulesImporter.Options { DragonRoot = rules, OutputJson = output });
        var table = LandBrushTable.LoadOrEmpty(output);
        bool ok = report.RulesParsed > 0 && table.IsLoaded;
        json.Event("done", new()
        {
            ["ok"] = ok, ["output"] = Path.GetFullPath(output), ["rules_dir"] = Path.GetFullPath(rules),
            ["files"] = report.FilesScanned, ["rules"] = report.RulesParsed, ["skipped"] = report.RulesSkipped,
            ["brushes"] = table.Brushes.Count, ["unknown_biomes"] = report.UnknownBiomes,
        });
        return ok ? 0 : 1;
    }

    /// <summary>The folder holding Dragon's rule files: DIR itself, or a Scripts/map below it.</summary>
    private static string? FindRules(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        static bool HasRules(string d) => Directory.Exists(d) && Directory.EnumerateFiles(d, "*2*.txt").Any();
        if (HasRules(dir)) return dir;
        return Directory.EnumerateDirectories(dir, "map", SearchOption.AllDirectories)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(Path.GetDirectoryName(d)), "Scripts", StringComparison.OrdinalIgnoreCase) && HasRules(d));
    }
}
