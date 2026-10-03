using System.Globalization;
using System.Text.Json;

namespace CentrED.MapMiner.Mining;

// Imports DragonMod_Imod13/Scripts/map/*.txt transition rules into our LandBrush JSON.
// Dragon's format is plaintext, one rule per line:
//
//     BAAAAABB 0037 000 000     // <8-char A/B mask> <hex tileID> <z1> <z2>
//
// where the 8-char string represents the surrounding cells in clockwise-from-NW order:
//
//     pos 0 1 2 3 4 5 6 7
//     dir NW N NE E SE S SW W
//
// 'A' = self biome, 'B' = other biome. Filenames encode the biome pair as
// "<self>2<other>(-variant)?.txt" (e.g. "grass2sand.txt", "grass2water-light.txt").
//
// Output: a single landbrush.json compatible with LandBrushTable.LoadOrEmpty(...).
// Variants ("-light", "-dark", "-dark-2") flow into separate target brushes
// (Water, WaterDeep, WaterDeep2) so the gen pass can pick by elevation context.
public static class DragonRulesImporter
{
    public sealed class Options
    {
        public required string DragonRoot { get; init; }    // .../Dragon_Imod13/Scripts/map
        public required string OutputJson { get; init; }
        public string? AliasJson { get; init; }              // optional override; falls back to DefaultAliases
        public bool MergeWithExisting { get; init; } = false; // if true, load OutputJson first and add to it
    }

    public sealed class Report
    {
        public int FilesScanned;
        public int RulesParsed;
        public int RulesSkipped;
        public int BrushesEmitted;
        public List<string> UnknownBiomes { get; } = new();
        public List<string> SkippedFiles { get; } = new();
    }

    // Dragon biome tag → our brush vocabulary. Keys are filename-segments before/after the
    // "2" separator, lowercased. Variants ("-light"/"-dark"/...) are post-processed separately.
    private static readonly Dictionary<string, string> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grass"]       = "Grassland",
        ["grassb"]      = "GrassRough",
        ["grassbump"]   = "GrassRough",
        ["furrows"]     = "Furrows",
        ["forest"]      = "Forest",
        ["jungle"]      = "Jungle",
        ["swamp"]       = "Swamp",
        ["sand"]        = "Beach",
        ["sandstone"]   = "Sandstone",
        ["red"]         = "RedSand",
        ["water"]       = "Water",
        ["mountain"]    = "Mountain",
        ["cave"]        = "Cave",
        ["cave-ent"]    = "CaveEntrance",
        ["cave-enti"]   = "CaveEntranceInterior",
        ["dirt"]        = "Dirt",
        ["cobble"]      = "Cobble",
        ["wasteland"]   = "Wasteland",
        ["snow"]        = "Snow",
        ["snowforest"]  = "SnowForest",
        ["snowmeadow"]  = "SnowMeadow",
        ["glacier"]     = "Glacier",
        ["dun"]         = "Dun",
        ["black"]       = "Black",
        ["lava"]        = "Lava",
    };

    // Variant suffix (after the "<self>2<other>" prefix) → target-brush suffix.
    // grass2water-light → Grassland → Water
    // grass2water-dark  → Grassland → WaterDeep
    // grass2water-dark-2 → Grassland → WaterDeep2
    private static readonly Dictionary<string, string> VariantSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        [""]       = "",
        ["light"]  = "",         // shallow / cliff coast — the canonical "Water" rules
        ["dark"]   = "Deep",
        ["dark-2"] = "Deep2",
    };

    // Dragon-string position → our 8-bit Direction layout.
    // Our bit order (from LandTransitionPass.dirOffsets): N=0, NE=1, E=2, SE=3, S=4, SW=5, W=6, NW=7.
    // Dragon position order: NW=0, N=1, NE=2, E=3, SE=4, S=5, SW=6, W=7.
    private static readonly int[] DragonPosToOurBit = { 7, 0, 1, 2, 3, 4, 5, 6 };

    public static Report Run(Options opt, Action<string>? log = null)
    {
        log ??= _ => { };
        var report = new Report();
        if (!Directory.Exists(opt.DragonRoot))
        {
            log($"Dragon rule root not found: {opt.DragonRoot}");
            return report;
        }

        var aliases = LoadAliases(opt.AliasJson) ?? DefaultAliases;
        var brushes = opt.MergeWithExisting ? LoadExistingOrEmpty(opt.OutputJson) : new(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(opt.DragonRoot, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fname = Path.GetFileNameWithoutExtension(path);
            if (!TryParseFilename(fname, out var selfTag, out var otherTag, out var variant))
            {
                report.SkippedFiles.Add(fname);
                continue;
            }
            if (!aliases.TryGetValue(selfTag, out var selfBrush))
            {
                if (!report.UnknownBiomes.Contains(selfTag)) report.UnknownBiomes.Add(selfTag);
                selfBrush = Cap(selfTag);
            }
            if (!aliases.TryGetValue(otherTag, out var otherBrush))
            {
                if (!report.UnknownBiomes.Contains(otherTag)) report.UnknownBiomes.Add(otherTag);
                otherBrush = Cap(otherTag);
            }
            // Variant tweak — only documented for grass2water; others fall through unchanged.
            if (!string.IsNullOrEmpty(variant))
            {
                if (VariantSuffixes.TryGetValue(variant, out var suffix) && !string.IsNullOrEmpty(suffix))
                    otherBrush += suffix;
                // unknown variants → keep the original brush name; they'll just stack as additional rules
            }

            int parsed = 0, skipped = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (TryParseFullRule(line, out byte mask, out ushort[] tileIds, out int zMin, out int zMax))
                {
                    foreach (ushort tileId in tileIds) AddTransition(brushes, selfBrush, otherBrush, mask, tileId, zMin, zMax);
                    parsed++;
                }
                else
                {
                    skipped += string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//") ? 0 : 1;
                }
            }
            report.RulesParsed += parsed;
            report.RulesSkipped += skipped;
            report.FilesScanned++;
            log($"  {fname}: {selfBrush}→{otherBrush}, parsed {parsed} rules ({skipped} unparseable)");
        }

        report.BrushesEmitted = brushes.Count;
        WriteOutput(opt.OutputJson, brushes, opt.DragonRoot);
        log($"Wrote {opt.OutputJson}: {brushes.Count} brushes, {report.RulesParsed} rules from {report.FilesScanned} files.");
        return report;
    }

    // Parse "grass2water-light" → ("grass", "water", "light"). Returns false if no "2" delimiter.
    internal static bool TryParseFilename(string name, out string self, out string other, out string variant)
    {
        self = other = variant = "";
        int sep = name.IndexOf('2');
        if (sep <= 0 || sep >= name.Length - 1) return false;
        self = name.Substring(0, sep);
        var rest = name.Substring(sep + 1);
        int dash = rest.IndexOf('-');
        if (dash < 0) { other = rest; return true; }
        other = rest.Substring(0, dash);
        variant = rest.Substring(dash + 1);
        return true;
    }

    // Parse a Dragon rule line. Tolerant of comment lines, trailing comments, and blank lines.
    // Returns true with mask + tileId on success.
    internal static bool TryParseRule(string raw, out byte mask, out ushort tileId)
    {
        bool ok = TryParseFullRule(raw, out mask, out var tiles, out _, out _);
        tileId = ok ? tiles[0] : (ushort)0;
        return ok;
    }

    internal static bool TryParseFullRule(string raw, out byte mask, out ushort[] tiles, out int zMin, out int zMax)
    {
        mask = 0; tiles = Array.Empty<ushort>(); zMin = zMax = 0;
        int comment = raw.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) raw = raw[..comment];
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // The last two columns are decimal altitude additions, never tile IDs.
        if (parts.Length < 4 || parts[0].Length != 8) return false;
        for (int i = 0; i < 8; i++)
        {
            if (parts[0][i] == 'B') mask |= (byte)(1 << DragonPosToOurBit[i]);
            else if (parts[0][i] != 'A') return false;
        }
        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out zMin)
            || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out zMax)
            || zMin < -128 || zMax > 127 || zMin > zMax) return false;
        var parsed = new List<ushort>();
        for (int i = 1; i < parts.Length - 2; i++)
        {
            if (!ushort.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) || id == 0) return false;
            parsed.Add(id);
        }
        tiles = parsed.ToArray();
        return tiles.Length > 0;
    }

    private static void AddTransition(Dictionary<string, BrushOut> brushes, string self, string other, byte mask, ushort tileId, int zMin, int zMax)
    {
        if (!brushes.TryGetValue(self, out var brush)) brushes[self] = brush = new BrushOut { Name = self };
        if (!brush.Transitions.TryGetValue(other, out var list)) brush.Transitions[other] = list = new List<TransitionOut>();
        if (list.Any(e => e.Direction == mask && e.TileID == tileId && e.AltitudeMin == zMin && e.AltitudeMax == zMax)) return;
        list.Add(new TransitionOut { TileID = tileId, Direction = mask, AltitudeMin = zMin, AltitudeMax = zMax });
    }

    private static Dictionary<string, string>? LoadAliases(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    d[p.Name] = p.Value.GetString() ?? p.Name;
            return d;
        }
        catch { return null; }
    }

    private static Dictionary<string, BrushOut> LoadExistingOrEmpty(string path)
    {
        var brushes = new Dictionary<string, BrushOut>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return brushes;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            foreach (var bp in doc.RootElement.EnumerateObject())
            {
                if (bp.Name.StartsWith("_")) continue;
                if (bp.Value.ValueKind != JsonValueKind.Object) continue;
                var brush = new BrushOut { Name = bp.Name };
                if (bp.Value.TryGetProperty("Tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array)
                    foreach (var tid in tiles.EnumerateArray()) brush.Tiles.Add((ushort)tid.GetInt32());
                if (bp.Value.TryGetProperty("Transitions", out var trs) && trs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tp in trs.EnumerateObject())
                    {
                        var list = new List<TransitionOut>();
                        foreach (var tEl in tp.Value.EnumerateArray())
                        {
                            list.Add(new TransitionOut
                            {
                                TileID = (ushort)tEl.GetProperty("TileID").GetInt32(),
                                Direction = tEl.TryGetProperty("Direction", out var d) ? (byte)d.GetInt32() : (byte)0,
                                AltitudeMin = tEl.TryGetProperty("AltitudeMin", out var mn) ? mn.GetInt32() : 0,
                                AltitudeMax = tEl.TryGetProperty("AltitudeMax", out var mx) ? mx.GetInt32() : 0,
                            });
                        }
                        brush.Transitions[tp.Name] = list;
                    }
                }
                brushes[bp.Name] = brush;
            }
        }
        catch { /* fall through with empty */ }
        return brushes;
    }

    private static void WriteOutput(string path, Dictionary<string, BrushOut> brushes, string sourceRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        ordered["_provenance"] = new
        {
            imported_from = sourceRoot.Replace('\\', '/'),
            imported_license = "DragonMod_Imod13 Scripts/map (rule files)",
            imported_at = DateTime.UtcNow.ToString("o"),
        };
        foreach (var (name, brush) in brushes.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            ordered[name] = brush;
        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNamingPolicy = null,
        });
        File.WriteAllText(path, json);
    }

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    public sealed class BrushOut
    {
        public string Name { get; set; } = "";
        public List<ushort> Tiles { get; set; } = new();
        public Dictionary<string, List<TransitionOut>> Transitions { get; set; } = new();
    }

    public sealed class TransitionOut
    {
        public ushort TileID { get; set; }
        public byte Direction { get; set; }
        public int AltitudeMin { get; set; }
        public int AltitudeMax { get; set; }
    }
}
