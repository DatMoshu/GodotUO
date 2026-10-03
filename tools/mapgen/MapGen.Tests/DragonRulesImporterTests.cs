using System.Text.Json;
using CentrED.MapMiner.Mining;

namespace CentrED.MapGen.Tests;

public class DragonRulesImporterTests
{
    [Fact]
    public void AlternateTilesAndDecimalAltitudeBoundsSurviveImportAndMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dragon-import-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "grass2mountain.txt"), "BAAAAAAA 0003 0005 003 007 // two variants\n");
            string output = Path.Combine(dir, "brush.json");
            var report = DragonRulesImporter.Run(new() { DragonRoot = dir, OutputJson = output });
            Assert.Equal(1, report.RulesParsed); Assert.Equal(0, report.RulesSkipped);
            DragonRulesImporter.Run(new() { DragonRoot = dir, OutputJson = output, MergeWithExisting = true });
            using var doc = JsonDocument.Parse(File.ReadAllText(output));
            var rows = doc.RootElement.GetProperty("Grassland").GetProperty("Transitions").GetProperty("Mountain").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(new[] { 3, 5 }, rows.Select(r => r.GetProperty("TileID").GetInt32()));
            Assert.All(rows, r => { Assert.Equal(128, r.GetProperty("Direction").GetInt32()); Assert.Equal(3, r.GetProperty("AltitudeMin").GetInt32()); Assert.Equal(7, r.GetProperty("AltitudeMax").GetInt32()); });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CaveAndRedSandAreNotMergedIntoMountainAndBeach()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dragon-alias-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "cave2dirt.txt"), "ABAAAAAA 0244 000 000\n");
            File.WriteAllText(Path.Combine(dir, "sand2red.txt"), "ABAAAAAA 0016 000 000\n");
            string output = Path.Combine(dir, "brush.json");
            DragonRulesImporter.Run(new() { DragonRoot = dir, OutputJson = output });
            using var doc = JsonDocument.Parse(File.ReadAllText(output));
            Assert.True(doc.RootElement.TryGetProperty("Cave", out _));
            Assert.False(doc.RootElement.TryGetProperty("Mountain", out _));
            Assert.True(doc.RootElement.GetProperty("Beach").GetProperty("Transitions").TryGetProperty("RedSand", out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidAltitudeRangeIsReportedInsteadOfBecomingTileIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dragon-invalid-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "grass2mountain.txt"), "BAAAAAAA 0003 007 003\n");
            var report = DragonRulesImporter.Run(new() { DragonRoot = dir, OutputJson = Path.Combine(dir, "brush.json") });
            Assert.Equal(0, report.RulesParsed); Assert.Equal(1, report.RulesSkipped);
        }
        finally { Directory.Delete(dir, true); }
    }
}
