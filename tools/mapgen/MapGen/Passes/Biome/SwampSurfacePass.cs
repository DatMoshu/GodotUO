using System.Globalization;
using System.Xml.Linq;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class SwampSurfaceParams
{
    [TunableDisplay("Transition catalogue", Tooltip = "guo (GUO's transition table: Grassland>Swamp and Swamp>Bog, the default), or a folder of UO Landscaper transition XML (e.g. mined/landscaper-transitions after guo-mapgen prepare --landscaper DIR). A folder that is not found falls back to guo.")]
    public string CataloguePath { get; set; } = "guo";
    [TunableDisplay("Moss border width", Tooltip = "Moss tiles separate swamp interiors from grass; the two surface families must never be randomly mixed.")]
    [TunableRange(2, 8)]
    public int MossBorderWidth { get; set; } = 2;
}

/// <summary>Separate moss and swamp surfaces, then paint their oriented borders.</summary>
public sealed class SwampSurfacePass : IGenerationPass
{
    public string Name => "Swamp Surface";
    public string Category => "Biome";
    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId;
    public object CreateDefaultParams() => new SwampSurfaceParams();
    private static readonly ushort[] Moss = { 0x3DED, 0x3DEE, 0x3DEF, 0x3DF0 };
    private static readonly ushort[] Swamp = { 0x3DE9, 0x3DEA, 0x3DEB, 0x3DEC };
    private sealed record Rule(byte[] Cells, ushort[] Tiles);

    public void Run(GenContext ctx, object parameters)
    {
        var p = (SwampSurfaceParams)parameters; var ir = ctx.IR;
        if (ir.Biome is null || ir.LandId is null || ir.Height_Z is null) return;
        Dictionary<byte, Dictionary<byte, ushort[]>>? guo = null;
        string source;
        string root = p.CataloguePath.Equals("guo", StringComparison.OrdinalIgnoreCase) ? "" : RepoRootResolver.Resolve(p.CataloguePath);
        string[] files = { Path.Combine(root, "land/1-Grass/1-Grass_To_50-Moss.xml"), Path.Combine(root, "Wild/Swamp/Moss -- Swamp.xml") };
        if (root.Length == 0 || files.Any(f => !File.Exists(f)))
        {
            if (root.Length > 0) ctx.Report.Notes.Add($"Swamp Surface: no transition catalogue at {p.CataloguePath}; using GUO's transition table");
            guo = GuoEdges(out source);
            if (guo is null)
            {
                ctx.Report.Warnings.Add($"Swamp Surface: {source} has no Grassland>Swamp and Swamp>Bog edges; surface unchanged");
                return;
            }
        }
        else source = root;
        var rules = new Dictionary<string, Rule>();
        if (guo is null)
        foreach (string file in files)
        foreach (var entry in XDocument.Load(file).Descendants("TransInfo"))
        {
            string key = (string?)entry.Attribute("HashKey") ?? "";
            if (key.Length != 18) continue;
            var cells = Enumerable.Range(0, 9).Select(i => byte.Parse(key.Substring(i * 2, 2), NumberStyles.HexNumber)).ToArray();
            var tiles = entry.Descendants("MapTile").Where(t => (int?)t.Attribute("AltIDMod") is null or 0)
                .Select(t => checked((ushort)(int)t.Attribute("TileID")!)).Distinct().ToArray();
            if (tiles.Length > 0) rules[key.ToUpperInvariant()] = new Rule(cells, tiles);
        }
        int w = ir.Scope.Width, h = ir.Scope.Height; var outside = new bool[w * h];
        int G(int i) => ir.Index(ir.Scope.X1 + i % w, ir.Scope.Y1 + i / w);
        for (int i = 0; i < outside.Length; i++) outside[i] = (BiomeId)ir.Biome[G(i)] is not (BiomeId.Swamp or BiomeId.Wetland);
        if (outside.All(v => v)) return;
        int band = Math.Clamp(p.MossBorderWidth, 2, 8);
        var distance = FieldOps.Distance4(outside, w, h, band + 1);
        var material = new byte[outside.Length];
        for (int i = 0; i < material.Length; i++)
            material[i] = !outside[i] ? distance[i] > band ? (byte)0x29 : (byte)0x32
                : (BiomeId)ir.Biome[G(i)] is BiomeId.Grassland or BiomeId.Savanna ? (byte)1 : (byte)0;
        var cache = new Dictionary<string, ushort[]?>(); int painted = 0, approximated = 0;
        var cells9 = new byte[9];
        for (int y = 0; y < h; y++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; byte own = material[i]; if (own == 0) continue;
                int gx = ir.Scope.X1 + x, gy = ir.Scope.Y1 + y, g = G(i);
                // These zero-offset surface rules describe flat ground. Keep
                // the cliff/coast art on stretched quads.
                if (gx + 1 >= ir.Width || gy + 1 >= ir.Height) continue;
                sbyte height = ir.Height_Z[g];
                if (ir.Height_Z[ir.Index(gx + 1, gy)] != height || ir.Height_Z[ir.Index(gx, gy + 1)] != height
                    || ir.Height_Z[ir.Index(gx + 1, gy + 1)] != height) continue;
                bool hasMoss = false, hasSwamp = false; int at = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    byte v = (uint)nx < (uint)w && (uint)ny < (uint)h ? material[ny * w + nx] : own;
                    // A foreign biome is not grass evidence. BufferSwamps normally
                    // prevents this, but keep its terrain untouched if it occurs.
                    cells9[at++] = v == 0 ? own : v;
                    hasMoss |= v == 0x32; hasSwamp |= v == 0x29;
                }
                if (own == 1 && !hasMoss) continue;
                ushort[]? pool = own == 0x29 ? Swamp : own == 0x32 ? Moss : null;
                if ((own == 1 && hasMoss) || (own == 0x32 && hasSwamp))
                {
                    if (guo is not null)
                    {
                        // GUO's table: the shape of the other side's neighbours picks the edge; a sliver keeps the interior.
                        byte other = own == 1 ? (byte)0x32 : (byte)0x29;
                        byte mask = 0;
                        for (int k = 0; k < 8; k++) if (cells9[MaskCell[k]] == other) mask |= (byte)(1 << k);
                        if (guo[own].TryGetValue(mask, out var tiles)) pool = tiles;
                        else approximated++;
                        if (pool is not { Length: > 0 }) continue;
                        ir.LandId[G(i)] = LatticePick.Pick(pool, ir.Scope.X1 + x, ir.Scope.Y1 + y, ir.Seed);
                        painted++;
                        continue;
                    }
                    string key = Convert.ToHexString(cells9);
                    if (!cache.TryGetValue(key, out var edge))
                    {
                        if (rules.TryGetValue(key, out var exact)) edge = exact.Tiles;
                        else
                        {
                            int best = int.MaxValue; var candidates = new List<ushort>();
                            foreach (var rule in rules.Values)
                            {
                                if (rule.Cells[4] != own) continue;
                                int mismatch = 0;
                                for (int k = 0; k < 9; k++) if (rule.Cells[k] != cells9[k]) mismatch++;
                                if (mismatch < best) { best = mismatch; candidates.Clear(); }
                                if (mismatch == best) candidates.AddRange(rule.Tiles);
                            }
                            edge = candidates.Count == 0 ? null : candidates.Distinct().ToArray();
                        }
                        cache[key] = edge;
                    }
                    if (!rules.ContainsKey(key)) approximated++;
                    if (edge is { Length: > 0 }) pool = edge;
                }
                if (pool is not { Length: > 0 }) continue;
                ir.LandId[G(i)] = LatticePick.Pick(pool, ir.Scope.X1 + x, ir.Scope.Y1 + y, ir.Seed);
                painted++;
            }
        }
        ctx.Report.TilesTouched = painted;
        ctx.Report.Notes.Add(guo is null
            ? $"separate moss/swamp surfaces={painted}; nearest-pattern fallback={approximated}; catalogue patterns={rules.Count}"
            : $"separate moss/swamp surfaces={painted}; slivers kept as interior={approximated}; edges from {source}");
    }

    /// <summary>The 3x3 cell (row-major, centre 4) of each EdgeShapes bit: N, NE, E, SE, S, SW, W, NW.</summary>
    private static readonly int[] MaskCell = { 1, 2, 5, 8, 7, 6, 3, 0 };

    /// <summary>
    /// Edges from GUO's transition table, per owner surface (1 grass, 0x32 moss) and neighbour mask: grass
    /// against moss is the table's Grassland>Swamp pair, moss against bog its Swamp>Bog pair. Each pair comes
    /// from the user's resolved table when it has it (a table resolved before the pair existed does not),
    /// else from the committed one.
    /// </summary>
    private static Dictionary<byte, Dictionary<byte, ushort[]>>? GuoEdges(out string source)
    {
        string committedPath = RepoRootResolver.Resolve(GuoTransitionTable.RelativePath), resolvedPath = LandBrushTable.DefaultPath();
        var committed = File.Exists(committedPath) ? GuoTransitionTable.Load(committedPath) : null;
        var resolved = resolvedPath != committedPath && File.Exists(resolvedPath) && GuoTransitionTable.IsGuoTable(resolvedPath)
            ? GuoTransitionTable.Load(resolvedPath) : null;
        source = "GUO's transition table";
        var result = new Dictionary<byte, Dictionary<byte, ushort[]>>();
        foreach (var (own, owner, other) in new[] { ((byte)1, "Grassland", "Swamp"), ((byte)0x32, "Swamp", "Bog") })
        {
            var pair = resolved?.Find(owner, other) is { Edges.Count: > 0 } r ? r : committed?.Find(owner, other);
            if (pair is null || pair.Edges.Count == 0) return null;
            var byMask = new Dictionary<byte, ushort[]>();
            for (int mask = 1; mask < 256; mask++)
                if (EdgeShapes.ShapeOf((byte)mask) is { } shape && pair.Edges.TryGetValue(shape, out var tiles))
                    byMask[(byte)mask] = tiles.SelectMany(t => Enumerable.Repeat(t.Id, t.Weight)).ToArray();
            result[own] = byMask;
        }
        return result;
    }
}
