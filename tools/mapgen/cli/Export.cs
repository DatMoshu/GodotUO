using System.Security.Cryptography;
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

        using var export = new StagedFolder(Path.Combine(runDir, "export"));
        using var worldOutput = worldProject is null ? null : new StagedFolder(worldProject);

        // The effective preset already holds every --set, --disable, --enable and --fast choice.
        var preset = MapGenPreset.Load(Path.Combine(runDir, "preset.json"));
        string? brushes = r.TryGetProperty("brushes", out var br) ? br.GetString() : null;
        var settings = new RunSettings("run", seed, width, height, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), brushes);
        var warnings = new List<string>();
        var steps = Pipeline.Build(preset, settings, warnings);
        json.Event("start", new() { ["run"] = Path.GetFullPath(runDir), ["width"] = width, ["height"] = height, ["seed"] = seed });
        var ir = Pipeline.Run(preset, settings, steps, warnings, (i, step, ms, _) =>
            json.Event("pass", new() { ["index"] = i, ["count"] = steps.Count, ["name"] = step.Pass.Name, ["enabled"] = step.Enabled, ["ms"] = ms }));

        string hash = MapHash.Of(ir);
        if (hash != expected)
            throw new CliError($"regenerated map differs from the run (hash {hash[..12]} vs {expected[..Math.Min(12, expected.Length)]}); export refused");

        json.Event("phase", new() { ["name"] = "Writing MUL files" });
        var mapDir = Path.Combine(export.WorkingPath, "map");
        var baseline = Path.Combine(export.WorkingPath, ".baseline");
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

        json.Event("phase", new() { ["name"] = "Reading MUL files back" });
        var verify = MulVerify.Check(ir, mapDir, facet);
        verify["hash"] = hash;
        verify["facet"] = facet;
        verify["width"] = width; verify["height"] = height;
        verify["land_tiles_written"] = result.LandTilesWritten;
        verify["statics_written"] = result.StaticsWritten;
        string[] files = { $"map/map{facet}.mul", $"map/staidx{facet}.mul", $"map/statics{facet}.mul" };
        verify["files"] = files;
        verify["sha256"] = files.ToDictionary(file => file, file =>
        {
            using var stream = File.OpenRead(Path.Combine(export.WorkingPath, file));
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        });
        File.WriteAllText(Path.Combine(export.WorkingPath, "export-verify.json"),
            JsonSerializer.Serialize(verify, new JsonSerializerOptions { WriteIndented = true }));

        bool ok = (bool)verify["ok"]!;
        Dictionary<string, object?>? world = null;
        if (ok)
        {
            if (worldOutput is not null)
            {
                json.Event("phase", new() { ["name"] = "Writing World project" });
                world = WorldProjectWriter.Write(ir, worldOutput.WorkingPath, facet, originX, originY, hash, runDir);
                worldOutput.Publish();
            }
            export.Publish();
        }
        json.Event("done", new()
        {
            ["ok"] = ok, ["hash"] = hash, ["export"] = ok ? export.Destination : null, ["verify"] = verify,
            ["world_project"] = worldProject is null ? null : Path.GetFullPath(worldProject), ["world"] = world,
        });
        return ok ? 0 : 1;
    }

    // Keep unfinished writes away from the public paths. A process killed during export can
    // leave a staging folder, but a retry always starts in a different one.
    private sealed class StagedFolder : IDisposable
    {
        public string Destination { get; }
        public string WorkingPath { get; }

        public StagedFolder(string destination)
        {
            Destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            OutputFolder.RequireFresh(Destination);
            WorkingPath = Path.Combine(Path.GetDirectoryName(Destination)!,
                $".{Path.GetFileName(Destination)}-mapgen-{Guid.NewGuid():N}");
            OutputFolder.PrepareFresh(WorkingPath);
        }

        public void Publish()
        {
            if (Directory.Exists(Destination))
                Directory.Delete(Destination); // only an empty destination is replaceable
            Directory.Move(WorkingPath, Destination);
        }

        public void Dispose()
        {
            if (Directory.Exists(WorkingPath))
                Directory.Delete(WorkingPath, recursive: true);
        }
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
        int blocks = (w / 8) * bh;
        if (map.Length != blocks * 196 || idx.Length != blocks * 12 || sta.Length % 7 != 0)
            throw new CliError("MUL verification failed: unexpected file lengths");

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
        int bytesRead = 0;
        for (int block = 0; block < blocks; block++)
        {
            int start = BitConverter.ToInt32(idx, block * 12), len = BitConverter.ToInt32(idx, block * 12 + 4);
            if (start == -1 && len == -1) continue;
            if (start != bytesRead || len <= 0 || len % 7 != 0 || len > sta.Length - start)
                throw new CliError($"MUL verification failed: invalid statics index for block {block}");
            int bx = block / bh, by = block % bh;
            for (int p = start; p < start + len; p += 7)
            {
                if (sta[p + 2] >= 8 || sta[p + 3] >= 8)
                    throw new CliError($"MUL verification failed: out-of-block static in block {block}");
                var key = (bx * 8 + sta[p + 2], by * 8 + sta[p + 3], (sbyte)sta[p + 4], BitConverter.ToUInt16(sta, p), BitConverter.ToUInt16(sta, p + 5));
                found[key] = found.GetValueOrDefault(key) + 1;
                foundCount++;
            }
            bytesRead += len;
        }
        if (bytesRead != sta.Length)
            throw new CliError("MUL verification failed: unindexed static data");
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
