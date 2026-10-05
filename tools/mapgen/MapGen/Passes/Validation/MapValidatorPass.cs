using CentrED.MapGen.Data;
using System.Text.Json;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Validation;
using CentrED.MapGen.Validation.Rules;

namespace CentrED.MapGen.Passes.Validation;

public sealed class MapValidatorParams
{
    // Ignored: the validator reads GenIR.SeaLevelZ / OceanZ. Kept so presets load.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Ocean Z (legacy, ignored)", Tooltip = "Ignored: ocean bodies are pinned to GenIR.OceanZ (sea level - 5, Felucca's -5).")]
    [TunableRange(-128, 127)]
    public int OceanZ { get; set; } = -5;

    [TunableDisplay("Edge band width", Tooltip = "Outer ring forced to water — skipped automatically when the map runs to its edge (band mostly land).")] [TunableRange(0, 64)]
    public int EdgeBandWidth { get; set; } = 8;

    [TunableDisplay("Z-step hard limit (engine)")] [TunableRange(1, 32)]
    public int ZStepHardLimit { get; set; } = 16;

    [TunableDisplay("Z-step warn threshold")] [TunableRange(1, 32)]
    public int ZStepWarnThreshold { get; set; } = 4;

    [TunableDisplay("Biome profile", Tooltip = "Which land-biome mix BiomeDistribution expects: felucca (default), desert, ice, or none (histogram only). Pick the planet's profile instead of turning validation off.")]
    public string BiomeProfile { get; set; } = "felucca";

    [TunableDisplay("Auto-fix")]
    public bool AutoFix { get; set; } = true;

    [TunableDisplay("Write report JSON to disk")]
    public bool WriteReport { get; set; } = false;

    [TunableDisplay("Report path")]
    public string ReportPath { get; set; } = "Data/map-mining/last-validation.json";
}

// Final pipeline pass. Applies all validation rules; auto-fixes determinate issues
// and emits warnings via the PassReport. Optionally writes a JSON report alongside.
public sealed class MapValidatorPass : IGenerationPass
{
    public string Name => "Map Validator";
    public string Category => "Validation";

    public IrFields Reads => IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.Height | IrFields.LandId | IrFields.StaticOps;

    public object CreateDefaultParams() => new MapValidatorParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MapValidatorParams)parameters;
        // Order: EdgeBand (may create water) -> ZStep (moves land only) -> FlatWater (pins
        // each water body to one Z) -> reports. ZStep used to run after FlatWater and pull
        // water cells off their plane.
        var rules = new List<IMapRule>
        {
            new EdgeBandRule(),
            new RiverBankRule(),
            new ZStepRule(),
            new FlatWaterRule(),
            new WaterStaticPlaneRule(),  // water statics back on the plane after every Z change
            new TreeInWaterRule(),
            new SwampGrassBufferRule(),  // F-6, report only (the buffer is built in Biome Assign)
            new CoastalGrassRule(),      // report only: coast cells left without an edge tile
            new BiomeDistributionRule(), // catches saturated/cratered moisture cascade
        };
        if (!p.AutoFix) foreach (var r in rules) r.Enabled = r.AutoFixable ? false : r.Enabled;

        var ruleCtx = new RuleContext
        {
            Rng = ctx.Rng,
            SeaLevelZ = ctx.IR.SeaLevelZ,
            OceanZ = ctx.IR.OceanZ,
            EdgeBandWidth = p.EdgeBandWidth,
            ZStepHardLimit = p.ZStepHardLimit,
            ZStepWarnThreshold = p.ZStepWarnThreshold,
            BiomeProfile = p.BiomeProfile,
        };

        var allFindings = new List<ValidationResult>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var r = rule.Validate(ctx.IR, ctx.IR.Scope, ruleCtx);
            allFindings.Add(r);
            if (r.Fixed > 0) ctx.Report.Notes.Add($"{rule.Name}: auto-fixed {r.Fixed}");
            if (r.Warned > 0) ctx.Report.Warnings.Add($"{rule.Name}: {r.Warned} warnings");
            if (r.Errors > 0) ctx.Report.Warnings.Add($"{rule.Name}: {r.Errors} errors");
            // Surface Info findings (e.g. BiomeDistributionRule's histogram) as Notes so
            // they show up in the run report alongside per-pass output.
            foreach (var f in r.Findings)
                if (f.Severity == ValidationSeverity.Info && !f.AutoFixed)
                    ctx.Report.Notes.Add($"{rule.Name}: {f.Message}");
            // Surface individual Warn/Error message bodies (not just counts) so the user
            // sees *what* failed, not just *that* something failed.
            foreach (var f in r.Findings)
                if (f.Severity is ValidationSeverity.Warn or ValidationSeverity.Error)
                    ctx.Report.Warnings.Add($"{rule.Name}: {f.Message}");
        }

        if (p.WriteReport)
        {
            try
            {
                // Repo-relative report paths land in the generator data folder, never in the repo.
                var reportPath = RepoRootResolver.Resolve(p.ReportPath);
                var dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(allFindings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(reportPath, json);
                ctx.Report.Notes.Add($"report -> {reportPath}");
            }
            catch (Exception e)
            {
                ctx.Report.Warnings.Add($"failed to write report: {e.Message}");
            }
        }
    }
}
