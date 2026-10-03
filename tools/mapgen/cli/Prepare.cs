using CentrED.MapGen.Data;
using CentrED.MapMiner.Mining;

namespace GuoMapGen;

/// <summary>
/// <c>prepare [--measure] [--dragon DIR] [--landscaper DIR]</c>: builds the generator's per-user data in the
/// generator data folder (UO_MAPGEN_DATA). GUO ships none of it (docs/upstream/mapgen.md).
/// <list type="bullet">
/// <item><c>--measure</c>: measures the transitions of the user's own Felucca and resolves GUO's
/// transition table against them (<see cref="MeasureCommand"/>). Without it the generator uses the
/// committed table's core tiles.</item>
/// <item><c>--dragon</c>: Dragon's <c>Scripts/map/*.txt</c> transition rules, converted by the owner's
/// importer into <c>landbrush.dragon.json</c>, for <c>run --brushes dragon</c> and coverage comparisons.</item>
/// <item><c>--landscaper</c>: UO Landscaper's <c>Data/Statics/*.xml</c> into <c>landscaper-statics/</c>
/// (Biome Static Scatter, when its Catalogue names that folder; GUO's scatter table is the default) and
/// its <c>Data/Transitions</c> into <c>landscaper-transitions/</c> (Swamp Surface, when its Transition
/// catalogue names that folder; GUO's transition table is the default).</item>
/// </list>
/// </summary>
public static class PrepareCommand
{
    public static int Execute(Args a, JsonEmitter json)
    {
        string? dragon = a.Get("dragon"), landscaper = a.Get("landscaper");
        bool measure = a.Flag("measure");
        if (dragon is null && landscaper is null && !measure)
            throw new CliError("prepare needs --measure (mine the transitions of your own Felucca), --dragon DIR (a Dragon install, or its Scripts/map folder) and/or --landscaper DIR (a UO Landscaper install or mod, holding Data/Statics and Data/Transitions)");

        bool ok = true;
        var done = new Dictionary<string, object?>();
        if (dragon is not null)
        {
            string rules = FindRules(dragon) ?? throw new CliError($"no Dragon transition rules (Scripts/map/*2*.txt) under {dragon}");
            string output = a.Get("out") ?? RepoRootResolver.Resolve(LandBrushTable.DataJsonRelativePath);
            var report = DragonRulesImporter.Run(new DragonRulesImporter.Options { DragonRoot = rules, OutputJson = output });
            var table = LandBrushTable.LoadOrEmpty(output);
            bool dragonOk = report.RulesParsed > 0 && table.IsLoaded;
            ok &= dragonOk;
            done["dragon"] = new Dictionary<string, object?>
            {
                ["ok"] = dragonOk, ["output"] = Path.GetFullPath(output), ["rules_dir"] = Path.GetFullPath(rules),
                ["files"] = report.FilesScanned, ["rules"] = report.RulesParsed, ["skipped"] = report.RulesSkipped,
                ["brushes"] = table.Brushes.Count, ["unknown_biomes"] = report.UnknownBiomes,
            };
        }

        if (landscaper is not null)
        {
            string data = FindLandscaperData(landscaper) ?? throw new CliError($"no UO Landscaper Data/Statics or Data/Transitions under {landscaper}");
            int statics = CopyTree(Path.Combine(data, "Statics"), RepoRootResolver.Resolve("mined/landscaper-statics"), "*.xml");
            int transitions = CopyTree(Path.Combine(data, "Transitions"), RepoRootResolver.Resolve("mined/landscaper-transitions"), "*.xml");
            bool landscaperOk = statics > 0 || transitions > 0;
            ok &= landscaperOk;
            done["landscaper"] = new Dictionary<string, object?>
            {
                ["ok"] = landscaperOk, ["data_dir"] = Path.GetFullPath(data), ["statics_files"] = statics, ["transition_files"] = transitions,
                ["output"] = Path.GetFullPath(RepoRootResolver.Resolve("mined/")),
            };
        }

        if (measure)
        {
            string clientData = a.Get("client-data") ?? Environment.GetEnvironmentVariable("UO_CLIENT_DATA")
                ?? throw new CliError("--measure needs the client data: set UO_CLIENT_DATA or pass --client-data DIR");
            var m = MeasureCommand.Run(clientData, a.Int("region-width", 5120));
            ok &= m["ok"] is true;
            done["measure"] = m;
            if (m["ok"] is true) done["resolved"] = MeasureCommand.Resolve((string)m["summary"]!);
        }

        done["ok"] = ok;
        json.Event("done", done);
        return ok ? 0 : 1;
    }

    /// <summary>The Data folder of a Landscaper install or mod: DIR itself, or DIR/Data.</summary>
    private static string? FindLandscaperData(string dir)
    {
        foreach (string d in new[] { dir, Path.Combine(dir, "Data") })
            if (Directory.Exists(Path.Combine(d, "Statics")) || Directory.Exists(Path.Combine(d, "Transitions")))
                return d;
        return null;
    }

    /// <summary>Copies every matching file under <paramref name="from"/>, keeping the layout. 0 when there is no such folder.</summary>
    private static int CopyTree(string from, string to, string pattern)
    {
        if (!Directory.Exists(from)) return 0;
        int n = 0;
        foreach (string f in Directory.EnumerateFiles(from, pattern, SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
            n++;
        }
        return n;
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
