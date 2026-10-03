using System.Text.Json;
using CentrED.MapGen.Commit;
using CentrED.MapGen.IR;
using CentrED.MapGen.Presets;

namespace GuoMapGen;

/// <summary>
/// <c>export</c>: turn a run folder into client map files. The map is regenerated from the run's
/// effective preset, seed and size; its hash must equal run.json's (the generator is
/// deterministic), then the legacy MUL triad is written to &lt;run&gt;/export/map and read back
/// cell by cell. The result is export/export-verify.json. With --world-project, a verified map is
/// also written as a world project's blocks (WorldProjectWriter) for the editor's World tab.
/// </summary>
public static class ExportCommand
{
    public static int Execute(Args a, JsonEmitter json)
    {
        var runDir = a.Get("run") ?? throw new CliError("export needs --run DIR (a folder written by run)");
        int facet = a.Int("facet", 0);
        if (facet is < 0 or > 5) throw new CliError("--facet must be 0..5");
        string? worldProject = a.Get("world-project");
        int originX = a.Int("origin-x", 0), originY = a.Int("origin-y", 0);

        var runJsonPath = Path.Combine(runDir, "run.json");
        if (!File.Exists(runJsonPath)) throw new CliError($"not a run folder (no run.json): {runDir}");
        using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
        var r = doc.RootElement;
        int width = r.GetProperty("width").GetInt32(), height = r.GetProperty("height").GetInt32();
        long seed = r.GetProperty("seed").GetInt64();
        string expected = r.GetProperty("hash").GetString() ?? "";

        var exportDir = Path.Combine(runDir, "export");
        OutputFolder.PrepareFresh(exportDir);

        // The effective preset already holds every --set, --disable, --enable and --fast choice.
        var preset = MapGenPreset.Load(Path.Combine(runDir, "preset.json"));
        var settings = new RunSettings("run", seed, width, height, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var warnings = new List<string>();
        var steps = Pipeline.Build(preset, settings, warnings);
        json.Event("start", new() { ["run"] = Path.GetFullPath(runDir), ["width"] = width, ["height"] = height, ["seed"] = seed });
        var ir = Pipeline.Run(preset, settings, steps, warnings, (i, step, ms, _) =>
            json.Event("pass", new() { ["index"] = i, ["count"] = steps.Count, ["name"] = step.Pass.Name, ["enabled"] = step.Enabled, ["ms"] = ms }));

        string hash = MapHash.Of(ir);
        if (hash != expected)
            throw new CliError($"regenerated map differs from the run (hash {hash[..12]} vs {expected[..Math.Min(12, expected.Length)]}); export refused");

        var mapDir = Path.Combine(exportDir, "map");
        var baseline = Path.Combine(exportDir, ".baseline");
        Directory.CreateDirectory(baseline);
        int bw = width / 8, bh = height / 8, blocks = bw * bh;
        File.WriteAllBytes(Path.Combine(baseline, "map.mul"), new byte[blocks * 196]);
        using (var index = new BinaryWriter(File.Create(Path.Combine(baseline, "staidx.mul"))))
            for (int b = 0; b < blocks; b++) { index.Write(-1); index.Write(-1); index.Write(0); }
        File.WriteAllBytes(Path.Combine(baseline, "statics.mul"), Array.Empty<byte>());

        var result = new StandaloneIrWriter().Write(ir, new StandaloneIrWriter.Options
        {
            SourceMapPath = Path.Combine(baseline, "map.mul"),
            SourceStaidxPath = Path.Combine(baseline, "staidx.mul"),
            SourceStaticsPath = Path.Combine(baseline, "statics.mul"),
            OutputDir = mapDir,
            BlockWidth = bw, BlockHeight = bh,
            ClearStaticsInScope = true,
            OutputMapName = $"map{facet}.mul",
            OutputStaidxName = $"staidx{facet}.mul",
            OutputStaticsName = $"statics{facet}.mul",
        });
        Directory.Delete(baseline, recursive: true);

        var verify = MulVerify.Check(ir, mapDir, facet);
        verify["hash"] = hash;
        verify["facet"] = facet;
        verify["width"] = width; verify["height"] = height;
        verify["land_tiles_written"] = result.LandTilesWritten;
        verify["statics_written"] = result.StaticsWritten;
        verify["files"] = new[] { $"map/map{facet}.mul", $"map/staidx{facet}.mul", $"map/statics{facet}.mul" };
        File.WriteAllText(Path.Combine(exportDir, "export-verify.json"),
            JsonSerializer.Serialize(verify, new JsonSerializerOptions { WriteIndented = true }));

        bool ok = (bool)verify["ok"]!;
        Dictionary<string, object?>? world = null;
        if (ok && worldProject is not null)
            world = WorldProjectWriter.Write(ir, worldProject, facet, originX, originY, hash, runDir);
        json.Event("done", new()
        {
            ["ok"] = ok, ["hash"] = hash, ["export"] = Path.GetFullPath(exportDir), ["verify"] = verify,
            ["world_project"] = worldProject is null ? null : Path.GetFullPath(worldProject), ["world"] = world,
        });
        return ok ? 0 : 1;
    }
}

/// <summary>Reads a written MUL triad back and compares it with the IR.</summary>
public static class MulVerify
{
    public static Dictionary<string, object?> Check(GenIR ir, string mapDir, int facet)
    {
        int w = ir.Width, h = ir.Height, bh = h / 8;
        var map = File.ReadAllBytes(Path.Combine(mapDir, $"map{facet}.mul"));
        var idx = File.ReadAllBytes(Path.Combine(mapDir, $"staidx{facet}.mul"));
        var sta = File.ReadAllBytes(Path.Combine(mapDir, $"statics{facet}.mul"));

        int landMismatch = 0, cells = 0;
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            int block = (x / 8) * bh + (y / 8);
            int off = block * 196 + 4 + ((y % 8) * 8 + (x % 8)) * 3;
            ushort id = BitConverter.ToUInt16(map, off);
            sbyte z = (sbyte)map[off + 2];
            int i = y * w + x;
            cells++;
            if (id != ir.LandId![i] || z != ir.Height_Z![i]) landMismatch++;
        }

        var expected = new Dictionary<(int, int, sbyte, ushort, ushort), int>();
        foreach (var o in ir.StaticOps)
        {
            var key = ((int)o.X, (int)o.Y, o.Z, o.Id, o.Hue);
            if (o.Kind == StaticOpKind.Add) expected[key] = expected.GetValueOrDefault(key) + 1;
            else
            {
                var k = expected.Keys.FirstOrDefault(e => e.Item1 == o.X && e.Item2 == o.Y && e.Item4 == o.Id && e.Item3 == o.Z);
                if (expected.TryGetValue(k, out var c)) { if (c <= 1) expected.Remove(k); else expected[k] = c - 1; }
            }
        }
        int expectedCount = expected.Values.Sum();

        var found = new Dictionary<(int, int, sbyte, ushort, ushort), int>();
        int foundCount = 0;
        for (int block = 0; block * 12 + 12 <= idx.Length; block++)
        {
            int start = BitConverter.ToInt32(idx, block * 12), len = BitConverter.ToInt32(idx, block * 12 + 4);
            if (start < 0 || len <= 0) continue;
            int bx = block / bh, by = block % bh;
            for (int p = start; p + 7 <= start + len && p + 7 <= sta.Length; p += 7)
            {
                var key = (bx * 8 + sta[p + 2], by * 8 + sta[p + 3], (sbyte)sta[p + 4], BitConverter.ToUInt16(sta, p), BitConverter.ToUInt16(sta, p + 5));
                found[key] = found.GetValueOrDefault(key) + 1;
                foundCount++;
            }
        }
        int staticMismatch = 0;
        foreach (var (k, c) in expected) staticMismatch += Math.Abs(c - found.GetValueOrDefault(k));
        foreach (var (k, c) in found) if (!expected.ContainsKey(k)) staticMismatch += c;

        return new Dictionary<string, object?>
        {
            ["ok"] = landMismatch == 0 && staticMismatch == 0,
            ["land_cells_checked"] = cells,
            ["land_mismatches"] = landMismatch,
            ["statics_expected"] = expectedCount,
            ["statics_found"] = foundCount,
            ["static_mismatches"] = staticMismatch,
        };
    }
}
