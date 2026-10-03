using System.Text.Json;

namespace CentrED.MapGen.Data;

/// <summary>Exact material-neighbour masks mined from a local reference profile.</summary>
public sealed class MeasuredTransitionTable
{
    public Dictionary<string, Dictionary<byte, ushort[]>> Pairs { get; } = new();
    public int MaskCount => Pairs.Values.Sum(p => p.Count);

    public static MeasuredTransitionTable Load(string path, int minimumSupport)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported transition atlas schema");
        var result = new MeasuredTransitionTable();
        foreach (var pair in doc.RootElement.GetProperty("pairs").EnumerateObject())
        {
            var masks = new Dictionary<byte, ushort[]>();
            foreach (var mask in pair.Value.GetProperty("rules").EnumerateObject())
            {
                byte direction = byte.Parse(mask.Name);
                var rows = mask.Value.EnumerateArray().Select(v => (Tile: v[0].GetInt32(), Count: v[1].GetInt32())).ToArray();
                if (rows.Length == 0) continue;
                if (rows.Any(v => v.Tile <= 0 || v.Tile >= 0x4000 || v.Count <= 0)) throw new InvalidDataException("Invalid measured transition entry");
                if (rows.Sum(v => v.Count) < Math.Max(1, minimumSupport)) continue;
                int peak = rows.Max(v => v.Count);
                // Bounded integer weights preserve common variants without
                // allocating arrays proportional to an entire reference map.
                var choices = new List<ushort>();
                foreach (var row in rows.Where(v => v.Count >= Math.Max(5, peak / 10)))
                {
                    int weight = Math.Max(1, (int)Math.Round(32.0 * row.Count / peak));
                    for (int i = 0; i < weight; i++) choices.Add((ushort)row.Tile);
                }
                if (choices.Count > 0) masks[direction] = choices.ToArray();
            }
            result.Pairs[pair.Name] = masks;
        }
        return result;
    }

    public ushort[]?[]? Overlay(string owner, string other, ushort[]?[]? fallback)
    {
        if (!Pairs.TryGetValue(owner + ">" + other, out var measured) || measured.Count == 0) return fallback;
        // Never mutate LandBrushTable's shared cached lookup.
        var result = fallback is null ? new ushort[]?[256] : (ushort[]?[])fallback.Clone();
        foreach (var (mask, tiles) in measured) result[mask] = tiles;
        return result;
    }
}
