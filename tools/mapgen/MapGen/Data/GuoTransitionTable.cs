using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentrED.MapGen.Data;

/// <summary>
/// GUO's own land transition table, format <c>guo.mapgen.transitions/1</c> (docs/data_formats.md §26, "Transition table").
/// The committed table (<see cref="RelativePath"/>) holds what GUO authors: the pairs, which side owns
/// each edge, a core tile family per pair, height offsets, bridges and notes. <c>guo-mapgen prepare
/// --measure</c> merges the variants measured in the user's own client into a resolved copy in the data
/// folder (<see cref="ResolvedRelativePath"/>), which the generator prefers when it exists.
/// </summary>
public sealed class GuoTransitionTable
{
    public const string Format = "guo.mapgen.transitions/1";

    /// <summary>The committed table.</summary>
    public const string RelativePath = "tools/mapgen/MapGen/presets/transitions.guo.json";

    /// <summary>The user's resolved table in the generator data folder (UO_MAPGEN_DATA).</summary>
    public const string ResolvedRelativePath = "mined/transitions.guo.resolved.json";

    public sealed class Pair
    {
        public string Owner = "", Other = "";
        /// <summary>Shape name to weighted tile ids, in file order.</summary>
        public Dictionary<string, List<(ushort Id, int Weight)>> Edges = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Shape name (or "*") to the most an edge cell moves toward the other side's height.</summary>
        public Dictionary<string, int> Z = new(StringComparer.OrdinalIgnoreCase);
        public string? Via;
        public bool Plain;
        public bool Measure = true;
        public string Notes = "";
        public string Key => Owner + ">" + Other;
    }

    public sealed class ResolveRules
    {
        public int MinPairSamples = 200, MinShapeSamples = 20, MaxVariants = 4, Scale = 8;
        public double MinShare = 0.1;
    }

    public List<Pair> Pairs { get; } = new();
    public ResolveRules Resolve { get; } = new();
    public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source document, kept so a resolved copy carries every field the table had.</summary>
    private JsonObject? _doc;

    /// <summary>True when <paramref name="path"/> is a JSON file in this format.</summary>
    public static bool IsGuoTable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var f) && f.GetString() == Format;
        }
        catch (Exception) { return false; }
    }

    public static GuoTransitionTable Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"{path}: not a JSON object");
        return Parse(node, path);
    }

    public static GuoTransitionTable Parse(JsonObject doc, string where = "transition table")
    {
        if ((string?)doc["format"] != Format) throw new InvalidDataException($"{where}: format is not {Format}");
        var t = new GuoTransitionTable { _doc = doc };
        if (doc["resolve"] is JsonObject r)
        {
            t.Resolve.MinPairSamples = (int?)r["min_pair_samples"] ?? t.Resolve.MinPairSamples;
            t.Resolve.MinShapeSamples = (int?)r["min_shape_samples"] ?? t.Resolve.MinShapeSamples;
            t.Resolve.MinShare = (double?)r["min_share"] ?? t.Resolve.MinShare;
            t.Resolve.MaxVariants = (int?)r["max_variants"] ?? t.Resolve.MaxVariants;
            t.Resolve.Scale = (int?)r["scale"] ?? t.Resolve.Scale;
        }
        if (doc["materials"] is JsonObject mats)
            foreach (var (name, _) in mats) t.Materials.Add(name);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pn in doc["pairs"] as JsonArray ?? throw new InvalidDataException($"{where}: no pairs"))
        {
            if (pn is not JsonObject po) throw new InvalidDataException($"{where}: a pair is not an object");
            var p = new Pair
            {
                Owner = (string?)po["owner"] ?? throw new InvalidDataException($"{where}: a pair has no owner"),
                Other = (string?)po["other"] ?? throw new InvalidDataException($"{where}: a pair has no other"),
                Via = (string?)po["via"],
                Plain = (bool?)po["plain"] ?? false,
                Measure = (bool?)po["measure"] ?? true,
                Notes = (string?)po["notes"] ?? "",
            };
            string at = $"{where}: {p.Key}";
            if (!seen.Add(p.Key)) throw new InvalidDataException($"{at}: listed twice");
            if (t.Materials.Count > 0)
                foreach (var m in new[] { p.Owner, p.Other, p.Via })
                    if (m is not null && !t.Materials.Contains(m)) throw new InvalidDataException($"{at}: unknown material '{m}'");
            if (po["edges"] is JsonObject edges)
                foreach (var (shape, list) in edges)
                {
                    if (!EdgeShapes.TryDirection(shape, out _)) throw new InvalidDataException($"{at}: unknown shape '{shape}'");
                    var tiles = new List<(ushort, int)>();
                    foreach (var e in list as JsonArray ?? throw new InvalidDataException($"{at}.{shape}: not a list"))
                        tiles.Add(ParseTile(e, $"{at}.{shape}"));
                    if (tiles.Count > 0) p.Edges[shape] = tiles;
                }
            if (po["z"] is JsonObject z)
                foreach (var (shape, v) in z)
                {
                    if (shape != "*" && !EdgeShapes.TryDirection(shape, out _)) throw new InvalidDataException($"{at}: z for unknown shape '{shape}'");
                    int dz = (int?)v ?? 0;
                    if (dz is < -40 or > 40) throw new InvalidDataException($"{at}: z {dz} for {shape} is out of range");
                    p.Z[shape] = dz;
                }
            t.Pairs.Add(p);
        }
        return t;
    }

    private static (ushort, int) ParseTile(JsonNode? e, string at)
    {
        string? text; int weight = 1;
        if (e is JsonArray a && a.Count == 2) { text = (string?)a[0]; weight = (int?)a[1] ?? 0; }
        else text = (string?)e;
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            || id == 0 || id >= 0x4000)
            throw new InvalidDataException($"{at}: '{e?.ToJsonString()}' is not a land id 0x0001-0x3FFF");
        if (weight < 1 || weight > 1000) throw new InvalidDataException($"{at}: weight {weight} is out of range");
        return (id, weight);
    }

    public Pair? Find(string owner, string other) =>
        Pairs.FirstOrDefault(p => string.Equals(p.Owner, owner, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(p.Other, other, StringComparison.OrdinalIgnoreCase));

    /// <summary>The height offset for a shape: its own entry, else "*", else 0.</summary>
    public static int ZFor(Pair p, string shape) =>
        p.Z.TryGetValue(shape, out int v) ? v : p.Z.TryGetValue("*", out int all) ? all : 0;

    /// <summary>
    /// The brush engine's view: one transition per (shape, tile) with the shape's direction byte and
    /// the tile's weight, plus each pair's bridge, plain flag and height offsets.
    /// </summary>
    public LandBrushTable ToBrushTable()
    {
        var b = new LandBrushTable { Source = "guo" };
        foreach (var p in Pairs)
        {
            if (!b.Brushes.TryGetValue(p.Owner, out var brush)) b.Brushes[p.Owner] = brush = new LandBrushTable.Brush { Name = p.Owner };
            if (p.Edges.Count > 0)
            {
                var list = new List<LandBrushTable.Transition>();
                foreach (var (shape, dir) in EdgeShapes.All)
                    if (p.Edges.TryGetValue(shape, out var tiles))
                        foreach (var (id, w) in tiles)
                            list.Add(new LandBrushTable.Transition { TileID = id, Direction = dir, Weight = w, AltIDMod = (sbyte)ZFor(p, shape) });
                brush.Transitions[p.Other] = list;
            }
            if (p.Via is not null) b.Via[p.Key] = p.Via;
            if (p.Plain) b.Plain.Add(p.Key);
            if (p.Z.Count > 0)
                b.EdgeZ[p.Key] = EdgeShapes.All.Where(s => ZFor(p, s.Name) != 0).ToDictionary(s => s.Direction, s => (sbyte)ZFor(p, s.Name));
        }
        return b;
    }

    /// <summary>
    /// A copy with the measured variants merged in (the summary written by <c>prepare --measure</c>,
    /// <c>guo.mapgen.transition-measure/1</c>). For each pair that allows it and has enough samples, each
    /// shape's well-supported variants are added beside the core ids, weighted by how often the user's
    /// map uses them; core ids keep at least weight 1. Ids that are interior tiles of either side, water,
    /// or out of range are never added. Returns the new table and how many variants it added.
    /// </summary>
    public (GuoTransitionTable Table, int Added) ResolveWith(JsonObject measure, Func<ushort, bool> isInterior)
    {
        if ((string?)measure["schema"] != "guo.mapgen.transition-measure/1")
            throw new InvalidDataException("not a guo.mapgen.transition-measure/1 summary");
        var doc = (JsonObject)(_doc ?? throw new InvalidOperationException("table has no source document")).DeepClone();
        int added = 0;
        var mpairs = measure["pairs"] as JsonObject;
        foreach (var pn in (JsonArray)doc["pairs"]!)
        {
            var po = (JsonObject)pn!;
            var p = Find((string)po["owner"]!, (string)po["other"]!)!;
            if (!p.Measure || mpairs?[p.Key] is not JsonObject mp) continue;
            if (((int?)mp["samples"] ?? 0) < Resolve.MinPairSamples || mp["shapes"] is not JsonObject shapes) continue;
            var edges = po["edges"] as JsonObject ?? new JsonObject();
            po["edges"] = edges;
            foreach (var (shape, _) in EdgeShapes.All)
            {
                if (shapes[shape] is not JsonObject ms) continue;
                int count = (int?)ms["count"] ?? 0;
                if (count < Resolve.MinShapeSamples || ms["top"] is not JsonArray top || top.Count == 0) continue;
                var rows = top.Select(r => (Id: ParseTile(r![0], p.Key).Item1, N: (int)r![1]!))
                    .Where(r => r.N >= Resolve.MinShare * count && !isInterior(r.Id) && !TileFlags.IsWaterLandId(r.Id))
                    .Take(Resolve.MaxVariants).ToList();
                if (rows.Count == 0) continue;
                int peak = rows.Max(r => r.N);
                var have = p.Edges.TryGetValue(shape, out var core) ? core.Select(c => c.Id).ToHashSet() : new HashSet<ushort>();
                var list = edges[shape] as JsonArray ?? new JsonArray();
                edges[shape] = list;
                // Core ids take the measured weight of their own variant when the map uses them.
                for (int k = 0; k < list.Count; k++)
                {
                    var (cid, cw) = ParseTile(list[k], p.Key);
                    var hit = rows.FirstOrDefault(r => r.Id == cid);
                    if (hit.N > 0) list[k] = new JsonArray($"0x{cid:X4}", Math.Max(cw, Weight(hit.N, peak)));
                }
                foreach (var r in rows.Where(r => !have.Contains(r.Id)))
                {
                    list.Add(new JsonArray($"0x{r.Id:X4}", Weight(r.N, peak)));
                    added++;
                }
            }
        }
        doc["resolved_from"] = new JsonObject
        {
            ["measure"] = measure["source"]?.DeepClone(),
            ["added_variants"] = added,
        };
        return (Parse(doc, "resolved table"), added);

        int Weight(int n, int peak) => Math.Max(1, (int)Math.Round((double)Resolve.Scale * n / peak));
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, (_doc ?? throw new InvalidOperationException("table has no source document"))
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
