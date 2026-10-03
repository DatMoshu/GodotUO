namespace CentrED.MapGen.Data;

/// <summary>
/// GUO's transition geometry (docs/data_formats.md §26, "Transition table"). A cell on the
/// owner side of a border has one of twelve shapes, named by where the OTHER material lies:
/// four straight edges (N, E, S, W), four outer corners where two edges meet (NE, SE, SW, NW:
/// the owner cell pokes into the other material) and four inner corners where only one diagonal
/// neighbour is other (in_NE ...: the other material pokes into the owner side).
///
/// Directions are tile coordinates, the order of the brush Direction byte: N (0,-1) is bit 0,
/// then NE, E, SE, S, SW, W, NW clockwise. Each shape is the set of neighbours that may be
/// other material while that tile still fits. The transition engine picks the smallest shape
/// that contains a cell's mask, so a mask no shape contains (a one-tile sliver) has no tile.
/// </summary>
public static class EdgeShapes
{
    public const byte N = 1 << 0, NE = 1 << 1, E = 1 << 2, SE = 1 << 3, S = 1 << 4, SW = 1 << 5, W = 1 << 6, NW = 1 << 7;

    /// <summary>The twelve shapes, in the order the table file lists them.</summary>
    public static readonly (string Name, byte Direction)[] All =
    {
        ("N", NW | N | NE), ("E", NE | E | SE), ("S", SE | S | SW), ("W", SW | W | NW),
        ("NE", NW | N | NE | E | SE), ("SE", NE | E | SE | S | SW), ("SW", SE | S | SW | W | NW), ("NW", SW | W | NW | N | NE),
        ("in_NE", NE), ("in_SE", SE), ("in_SW", SW), ("in_NW", NW),
    };

    private static readonly Dictionary<string, byte> ByName =
        All.ToDictionary(s => s.Name, s => s.Direction, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Direction byte of a shape name; false for an unknown name.</summary>
    public static bool TryDirection(string name, out byte direction) => ByName.TryGetValue(name, out direction);

    /// <summary>The shape a neighbour mask resolves to (the smallest containing shape), or null for a sliver or an empty mask.</summary>
    public static string? ShapeOf(byte mask)
    {
        if (mask == 0) return null;
        string? best = null;
        int bestPop = int.MaxValue;
        foreach (var (name, dir) in All)
        {
            if ((dir & mask) != mask) continue;
            int pop = System.Numerics.BitOperations.PopCount(dir);
            if (pop < bestPop) { bestPop = pop; best = name; }
        }
        return best;
    }
}
