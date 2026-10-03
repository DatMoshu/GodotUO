#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Serializable brush recipe. IDs always refer to the user's own client data.</summary>
internal sealed class WorldBrush
{
    public int Size = 7, Density = 35, Spacing = 2, Seed = 1, Height, Strength = 1, MaxSlope = 127;
    public bool Square, Land, FixedHeight, KeepStatics = true, AvoidWater = true;
    public string Operation = "Paint", AllowedLand = "", Variants = "", Edges = "";
    public ushort Hue;

    internal string Validate(EditorData data)
    {
        if (data?.IsLoaded != true) return "Wait for client art to load";
        if (Operation != "Paint") return null;
        foreach (string raw in Variants.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parsed = ParseVariants(raw);
            if (parsed.Count != 1) return "Invalid variant: use ID:weight, with a positive weight";
            uint art = (Land ? 0 : EditorData.LandCount) + parsed[0].Id;
            if ((Land && parsed[0].Id >= EditorData.LandCount) || !data.HasArt(art)) return $"No {(Land ? "land" : "static")} art for 0x{parsed[0].Id:X4}";
        }
        foreach (string rule in Edges.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = rule.Trim().Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[0], out int mask) || mask < 0 || mask > 15
                || !TryId(pair[1], out ushort tile) || tile >= EditorData.LandCount || !data.HasArt(tile))
                return "Invalid terrain edge rule: use neighbor mask 0–15 = valid land ID";
        }
        return null;
    }

    internal static List<(ushort Id, int Weight)> ParseVariants(string text)
    {
        var result = new List<(ushort, int)>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Trim().Split(':');
            if (!TryId(pair[0], out ushort id) || (pair.Length > 1 && (!int.TryParse(pair[1], out int _) || int.Parse(pair[1]) <= 0))) continue;
            result.Add((id, pair.Length > 1 ? Math.Clamp(int.Parse(pair[1]), 1, 10000) : 1));
        }
        return result;
    }

    internal static bool TryId(string text, out ushort id)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id)
            : ushort.TryParse(text, out id);
    }

    internal IEnumerable<(int X, int Y)> Footprint(int x, int y)
    {
        int n = Math.Clamp(Size, 1, 31), start = -(n / 2);
        float centre = start + (n - 1) / 2f;
        for (int dx = start; dx < start + n; dx++)
        for (int dy = start; dy < start + n; dy++)
        {
            if (!Square && (dx - centre) * (dx - centre) + (dy - centre) * (dy - centre) > n * n / 4f) continue;
            if (x + dx >= 0 && y + dy >= 0) yield return (x + dx, y + dy);
        }
    }

    // A stable per-cell hash means moving the preview never re-rolls the scatter.
    internal uint Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ Seed * 83492791);
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b;
            return h ^ (h >> 16);
        }
    }

    internal bool AcceptCell(WorldData data, int x, int y)
    {
        BlockData b = data.Block(x >> 3, y >> 3);
        if (b == null) return false;
        int i = (y & 7) * 8 + (x & 7);
        if (AvoidWater && b.LandWet[i]) return false;
        if (AllowedLand.Length > 0 && !ParseVariants(AllowedLand).Any(v => v.Id == b.LandId[i])) return false;
        if (Operation == "Paint" && !Land && KeepStatics && b.Objs[i]?.Count > 0) return false;
        if (MaxSlope < 127)
        {
            int z = b.LandZ[i];
            if (Math.Abs(data.LandZ(x + 1, y) - z) > MaxSlope || Math.Abs(data.LandZ(x, y + 1) - z) > MaxSlope) return false;
        }
        if (Operation != "Paint") return true;
        int step = Math.Max(1, Spacing);
        return (Land || (x % step == 0 && y % step == 0)) && Hash(x, y) % 100 < Math.Clamp(Density, 0, 100);
    }

    internal ushort Choose(int x, int y, ushort fallback)
    {
        var variants = ParseVariants(Variants);
        if (variants.Count == 0) return fallback;
        int pick = (int)((Hash(x, y) / 100) % (uint)variants.Sum(v => v.Weight));
        foreach (var v in variants) { pick -= v.Weight; if (pick < 0) return v.Id; }
        return fallback;
    }

    internal bool Apply(WorldEditor editor, WorldData data, int facet, IEnumerable<(int X, int Y)> cells, ushort fallback)
    {
        var accepted = PlanCells(data, cells, fallback);
        var edits = new Dictionary<(int, int), Action<WorldBlock>>();
        foreach (var group in accepted.GroupBy(c => (c.X >> 3, c.Y >> 3)))
        {
            var captured = group.ToArray();
            // Heights are sampled before any blocks change, including across block edges.
            var smooth = captured.ToDictionary(c => c, c => (data.LandZ(c.X - 1, c.Y) + data.LandZ(c.X + 1, c.Y)
                + data.LandZ(c.X, c.Y - 1) + data.LandZ(c.X, c.Y + 1)) / 4);
            edits[group.Key] = b =>
            {
                foreach (var c in captured)
                {
                    int i = (c.Y & 7) * 8 + (c.X & 7);
                    ushort id = Choose(c.X, c.Y, fallback);
                    if (Operation == "Paint" && Land)
                    {
                        // Match both this stroke and the existing terrain family at its boundary.
                        var family = TerrainFamily(fallback);
                        bool Inside(int px, int py) => accepted.Contains((px, py)) || InFamily(data, px, py, family);
                        int mask = (Inside(c.X, c.Y - 1) ? 1 : 0) | (Inside(c.X + 1, c.Y) ? 2 : 0)
                            | (Inside(c.X, c.Y + 1) ? 4 : 0) | (Inside(c.X - 1, c.Y) ? 8 : 0);
                        foreach (string rule in Edges.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] pair = rule.Trim().Split('=');
                            if (pair.Length == 2 && int.TryParse(pair[0], out int m) && m == mask && TryId(pair[1], out ushort edge)) id = edge;
                        }
                        b.LandId[i] = id;
                    }
                    else if (Operation == "Paint")
                    {
                        sbyte z = (sbyte)Math.Clamp((FixedHeight ? 0 : b.LandZ[i]) + Height, -128, 127);
                        if (!b.Statics.Any(s => s.X == (c.X & 7) && s.Y == (c.Y & 7) && s.Id == id && s.Z == z))
                            b.Statics.Add(new WorldStatic { X = (byte)(c.X & 7), Y = (byte)(c.Y & 7), Id = id, Z = z, Hue = Hue });
                    }
                    else if (Operation == "Erase statics")
                        b.Statics.RemoveAll(s => s.X == (c.X & 7) && s.Y == (c.Y & 7) && s.Z >= data.Host.MinVisibleZ && s.Z <= data.Host.MaxVisibleZ);
                    else if (Operation == "Hue statics")
                    {
                        for (int j = 0; j < b.Statics.Count; j++)
                        {
                            var s = b.Statics[j];
                            if (s.X != (c.X & 7) || s.Y != (c.Y & 7) || s.Z < data.Host.MinVisibleZ || s.Z > data.Host.MaxVisibleZ) continue;
                            s.Hue = Hue; b.Statics[j] = s;
                        }
                    }
                    else
                    {
                        int z = b.LandZ[i];
                        int next = Operation switch { "Raise" => z + Strength, "Lower" => z - Strength, "Flatten" => Height,
                            "Smooth" => z + Math.Clamp(smooth[c] - z, -Strength, Strength), _ => z };
                        b.LandZ[i] = (sbyte)Math.Clamp(next, -128, 127);
                    }
                }
            };
        }
        return editor.EditBatch(facet, edits, $"{Operation}: {accepted.Count} cells");
    }

    private HashSet<ushort> TerrainFamily(ushort fallback)
    {
        var family = ParseVariants(Variants).Select(v => v.Id).ToHashSet(); family.Add(fallback);
        foreach (string rule in Edges.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = rule.Split('=');
            if (pair.Length == 2 && TryId(pair[1], out ushort id)) family.Add(id);
        }
        return family;
    }

    private static bool InFamily(WorldData data, int x, int y, HashSet<ushort> family)
    {
        BlockData block = data.Block(x >> 3, y >> 3);
        return block != null && family.Contains(block.LandId[(y & 7) * 8 + (x & 7)]);
    }

    /// <summary>Includes adjacent matching terrain when an edge recipe must reconnect an older stroke.</summary>
    internal HashSet<(int X, int Y)> PlanCells(WorldData data, IEnumerable<(int X, int Y)> cells, ushort fallback)
    {
        var result = cells.Distinct().Where(c => AcceptCell(data, c.X, c.Y)).ToHashSet();
        if (Operation != "Paint" || !Land || string.IsNullOrWhiteSpace(Edges)) return result;
        var family = TerrainFamily(fallback);
        foreach (var c in result.ToArray())
        foreach (var offset in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
        {
            var n = (X: c.X + offset.Item1, Y: c.Y + offset.Item2);
            if (n.X >= 0 && n.Y >= 0 && InFamily(data, n.X, n.Y, family)) result.Add(n);
        }
        return result;
    }
}
#endif
