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

    internal bool ChangesTerrain => Operation == "Paint" ? Land : Operation is "Raise" or "Lower" or "Flatten" or "Smooth";
    internal WorldBrush Snapshot() => (WorldBrush)MemberwiseClone();
    private static readonly (int X, int Y)[] Neighbors = { (0, -1), (1, 0), (0, 1), (-1, 0) };

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

    private HashSet<ushort> AllowedIds() => AllowedLand.Length == 0 ? null
        : ParseVariants(AllowedLand).Select(v => v.Id).ToHashSet();

    /// <summary>Which rule leaves a cell alone ("water", "occupied", ...), or null when the brush takes it.</summary>
    private string Rejection(WorldData data, int x, int y, HashSet<ushort> allowed, bool repair = false)
    {
        BlockData b = data.Block(x >> 3, y >> 3);
        if (b == null) return "outside";
        int i = (y & 7) * 8 + (x & 7);
        if (AvoidWater && b.LandWet[i]) return "water";
        if (allowed != null && !allowed.Contains(b.LandId[i])) return "land";
        if (Operation == "Paint" && !Land && KeepStatics && b.Objs[i]?.Count > 0) return "occupied";
        if (MaxSlope < 127)
        {
            int z = b.LandZ[i];
            foreach (var offset in Neighbors)
            {
                int nx = x + offset.X, ny = y + offset.Y;
                if (nx < 0 || ny < 0 || data.Block(nx >> 3, ny >> 3) == null) continue;
                if (Math.Abs(data.LandZ(nx, ny) - z) > MaxSlope) return "slope";
            }
        }
        if (Operation != "Paint" || repair) return null;
        int step = Math.Max(1, Spacing);
        return (Land || (x % step == 0 && y % step == 0)) && Hash(x, y) % 100 < Math.Clamp(Density, 0, 100) ? null : "density";
    }

    /// <summary>
    /// ED6: why a stroke changed nothing, in words, naming the setting to change. The rule that turned away the
    /// most cells is the one named.
    /// </summary>
    private string WhyNothing(IReadOnlyDictionary<string, int> rejected, int requested)
    {
        if (requested == 0) return "Nothing changed: the brush had no cells under it";
        if (rejected.Count == 0)
            return Operation switch
            {
                "Paint" when !Land => "Nothing changed: the same item already stands there at that height",
                "Erase statics" or "Hue statics" => $"Nothing changed: {Operation} found no item there (between Visible Z min and max)",
                _ => "Nothing changed: the ground there is already like that",
            };
        string top = rejected.MaxBy(r => r.Value).Key;
        bool one = requested == 1;
        return top switch
        {
            "occupied" => one
                ? "Nothing placed: that cell already has an item. Keep existing statics is on; untick it to place on top"
                : "Nothing placed: those cells already have items. Keep existing statics is on; untick it to place on top",
            "water" => "Nothing placed: the brush is over water and Avoid water is on; untick it to paint there",
            "land" => "Nothing placed: that ground is not in Allowed land IDs; clear the box to paint on any ground",
            "slope" => "Nothing placed: the ground is steeper than Maximum slope allows",
            "density" => "Nothing placed: Density and Spacing left these cells empty; raise Density or move the brush",
            _ => "Nothing placed: the brush is outside the map",
        };
    }

    internal ushort Choose(int x, int y, ushort fallback)
    {
        var variants = ParseVariants(Variants);
        return Choose(x, y, fallback, variants, variants.Sum(v => v.Weight));
    }

    private ushort Choose(int x, int y, ushort fallback, List<(ushort Id, int Weight)> variants, int weight)
    {
        if (weight == 0) return fallback;
        int pick = (int)((Hash(x, y) / 100) % (uint)weight);
        foreach (var v in variants) { pick -= v.Weight; if (pick < 0) return v.Id; }
        return fallback;
    }

    internal enum EditKind { Material, Height, Add, Erase, Hue }

    internal readonly record struct CellEdit(int X, int Y, EditKind Kind, ushort Id, sbyte Z, bool Repair = false);

    internal sealed record StrokePlan(IReadOnlyList<CellEdit> Cells, int Skipped, string Reason, ushort Hue, int MinZ, int MaxZ)
    {
        public bool Apply(WorldEditor editor, int facet, string operation)
        {
            var edits = new Dictionary<(int, int), Action<WorldBlock>>();
            foreach (var group in Cells.GroupBy(c => (c.X >> 3, c.Y >> 3)))
            {
                var batch = group.ToArray();
                edits.Add(group.Key, block =>
                {
                    foreach (var c in batch)
                    {
                        byte x = (byte)(c.X & 7), y = (byte)(c.Y & 7);
                        int i = y * 8 + x;
                        switch (c.Kind)
                        {
                            case EditKind.Material: block.LandId[i] = c.Id; break;
                            case EditKind.Height: block.LandZ[i] = c.Z; break;
                            case EditKind.Add:
                                block.Statics.Add(new WorldStatic { X = x, Y = y, Z = c.Z, Id = c.Id, Hue = Hue });
                                break;
                            case EditKind.Erase:
                                block.Statics.RemoveAll(s => s.X == x && s.Y == y && s.Z >= MinZ && s.Z <= MaxZ);
                                break;
                            case EditKind.Hue:
                                for (int j = 0; j < block.Statics.Count; j++)
                                {
                                    var item = block.Statics[j];
                                    if (item.X != x || item.Y != y || item.Z < MinZ || item.Z > MaxZ) continue;
                                    item.Hue = Hue;
                                    block.Statics[j] = item;
                                }
                                break;
                        }
                    }
                });
            }
            return editor.EditBatch(facet, edits, $"{operation}: {Cells.Count} changed, {Skipped} skipped");
        }
    }

    internal bool Apply(WorldEditor editor, WorldData data, int facet, IEnumerable<(int X, int Y)> cells, ushort fallback)
        => Plan(data, cells, fallback).Apply(editor, facet, Operation);

    internal StrokePlan Plan(WorldData data, IEnumerable<(int X, int Y)> cells, ushort fallback)
    {
        var requested = cells.ToHashSet();
        var edits = new List<CellEdit>();
        var variants = ParseVariants(Variants);
        int weight = variants.Sum(v => v.Weight);
        var edges = new Dictionary<int, ushort>();
        foreach (string rule in Edges.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = rule.Split('=');
            if (pair.Length == 2 && int.TryParse(pair[0], out int mask) && TryId(pair[1], out ushort id)) edges[mask] = id;
        }
        var family = variants.Select(v => v.Id).Concat(edges.Values).ToHashSet();
        family.Add(fallback);
        var rejected = new Dictionary<string, int>();
        var accepted = PlanCells(data, requested, AllowedIds(), family, edges.Count > 0, rejected);
        int minZ = data.Host.MinVisibleZ, maxZ = data.Host.MaxVisibleZ;
        foreach (var c in accepted)
        {
            BlockData block = data.Block(c.X >> 3, c.Y >> 3);
            int i = (c.Y & 7) * 8 + (c.X & 7);
            int z = block.LandZ[i];
            if (Operation == "Paint" && Land)
            {
                int mask = 0;
                for (int n = 0; edges.Count > 0 && n < Neighbors.Length; n++)
                {
                    var neighbor = (c.X + Neighbors[n].X, c.Y + Neighbors[n].Y);
                    if (accepted.Contains(neighbor) || InFamily(data, neighbor.Item1, neighbor.Item2, family)) mask |= 1 << n;
                }
                ushort id = edges.TryGetValue(mask, out ushort edge) ? edge : Choose(c.X, c.Y, fallback, variants, weight);
                if (block.LandId[i] != id) edits.Add(new CellEdit(c.X, c.Y, EditKind.Material, id, (sbyte)z, !requested.Contains(c)));
            }
            else if (Operation == "Paint")
            {
                ushort id = Choose(c.X, c.Y, fallback, variants, weight);
                sbyte height = (sbyte)Math.Clamp((FixedHeight ? 0 : z) + Height, -128, 127);
                var statics = data.OverlayBlock(c.X >> 3, c.Y >> 3)?.Statics;
                if (statics != null && !statics.Any(s => s.X == (c.X & 7) && s.Y == (c.Y & 7) && s.Id == id && s.Z == height))
                    edits.Add(new CellEdit(c.X, c.Y, EditKind.Add, id, height));
            }
            else if (Operation is "Erase statics" or "Hue statics")
            {
                bool erase = Operation == "Erase statics";
                var statics = data.OverlayBlock(c.X >> 3, c.Y >> 3)?.Statics;
                if (statics?.Any(s => s.X == (c.X & 7) && s.Y == (c.Y & 7) && s.Z >= minZ && s.Z <= maxZ && (erase || s.Hue != Hue)) == true)
                    edits.Add(new CellEdit(c.X, c.Y, erase ? EditKind.Erase : EditKind.Hue, 0, 0));
            }
            else
            {
                int next = Operation switch
                {
                    "Raise" => z + Strength,
                    "Lower" => z - Strength,
                    "Flatten" => Height,
                    "Smooth" => z + Math.Clamp(Neighbors.Sum(n =>
                        data.Block((c.X + n.X) >> 3, (c.Y + n.Y) >> 3) == null
                            ? z : data.LandZ(c.X + n.X, c.Y + n.Y)) / 4 - z, -Strength, Strength),
                    _ => z,
                };
                next = Math.Clamp(next, -128, 127);
                if (next != z) edits.Add(new CellEdit(c.X, c.Y, EditKind.Height, 0, (sbyte)next));
            }
        }
        int skipped = requested.Count - edits.Count(e => !e.Repair);
        return new StrokePlan(edits, skipped, edits.Count == 0 ? WhyNothing(rejected, requested.Count) : null, Hue, minZ, maxZ);
    }

    private static bool InFamily(WorldData data, int x, int y, HashSet<ushort> family)
    {
        BlockData block = data.Block(x >> 3, y >> 3);
        return block != null && family.Contains(block.LandId[(y & 7) * 8 + (x & 7)]);
    }

    /// <summary>Includes adjacent matching terrain when an edge recipe must reconnect an older stroke.</summary>
    private HashSet<(int X, int Y)> PlanCells(WorldData data, HashSet<(int X, int Y)> cells,
        HashSet<ushort> allowed, HashSet<ushort> family, bool hasEdges, Dictionary<string, int> rejected)
    {
        var result = new HashSet<(int X, int Y)>();
        foreach (var c in cells)
        {
            string reason = Rejection(data, c.X, c.Y, allowed);
            if (reason == null) result.Add(c);
            else rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
        }
        if (Operation != "Paint" || !Land || !hasEdges) return result;
        foreach (var c in result.ToArray())
        foreach (var offset in Neighbors)
        {
            var n = (X: c.X + offset.Item1, Y: c.Y + offset.Item2);
            if (n.X >= 0 && n.Y >= 0 && InFamily(data, n.X, n.Y, family) && Rejection(data, n.X, n.Y, allowed, repair: true) == null) result.Add(n);
        }
        return result;
    }
}
#endif
