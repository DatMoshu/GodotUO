using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;

namespace CentrED.MapGen.Passes.Network;

// Shared helpers for everything that lays a walkable surface into the land layer: road
// centerlines, town streets, mountain trails. One place for the tile families, the
// water/biome tests and the brush-driven edge tiles.
internal static class RoadPaint
{
    // Felucca dirt interior (0x71-0x78) and cobblestones (0x3E9-0x3EC). 0x518 — the
    // old cobble default — has no land texture.
    public static readonly ushort[] DirtTiles = { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78 };
    public static readonly ushort[] CobbleTiles = { 0x3E9, 0x3EA, 0x3EB, 0x3EC };

    /// <summary>Tile pool for a configured id: a known family expands to all its variants; 0x518 maps to cobblestones.</summary>
    public static ushort[] PoolFor(int tileId, ushort[] family)
    {
        if (tileId == 0x518 || tileId <= 0) return family;
        if (Array.IndexOf(family, (ushort)tileId) >= 0) return family;
        if (Array.IndexOf(DirtTiles, (ushort)tileId) >= 0) return DirtTiles;
        if (Array.IndexOf(CobbleTiles, (ushort)tileId) >= 0) return CobbleTiles;
        return new[] { (ushort)tileId };
    }

    public static bool IsWater(GenIR ir, int idx)
    {
        if (ir.Biome is not null && (BiomeId)ir.Biome[idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River)
            return true;
        return ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx]);
    }

    // Brush that owns the "<biome> -> Dirt/Cobble" transition; null = no transition (the
    // edge stays hard). Desert/Beach use the Beach brush; swamp and others have none.
    public static string? BrushFor(BiomeId id) => id switch
    {
        BiomeId.Grassland or BiomeId.Savanna => "Grassland",
        BiomeId.Forest or BiomeId.DenseForest => "Forest",
        BiomeId.Jungle => "Jungle",
        BiomeId.Snow or BiomeId.Tundra => "Snow",
        BiomeId.Beach or BiomeId.Desert => "Beach",
        BiomeId.Mountain or BiomeId.HighMountain => "Mountain",
        _ => null,
    };

    private static readonly (int dx, int dy, byte bit)[] DirOffsets =
    {
        (0, -1, 1 << 0), (1, -1, 1 << 1), (1, 0, 1 << 2), (1, 1, 1 << 3),
        (0,  1, 1 << 4), (-1, 1, 1 << 5), (-1, 0, 1 << 6), (-1, -1, 1 << 7),
    };

    // Dragon's rock border belongs to the dirt cell (Dirt -> Mountain).
    // Painting a guessed cliff on the neighbouring rock leaves raw dirt touching it.
    public static int PaintMountainEdges(GenIR ir, HashSet<int> road)
    {
        if (!ir.Brushes.IsLoaded || ir.LandId is null || ir.Biome is null) return 0;
        int count = 0;
        foreach (int i in road)
        {
            int x = i % ir.Width, y = i / ir.Width;
            byte mask = 0;
            foreach (var (dx, dy, bit) in DirOffsets)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) continue;
                if ((BiomeId)ir.Biome[ir.Index(nx, ny)] is BiomeId.Mountain or BiomeId.HighMountain) mask |= bit;
            }
            if (mask == 0) continue;
            ushort tile = ir.Brushes.PickTransitionTile("Dirt", "Mountain", mask, FieldOps.Hash(x, y, ir.Seed ^ 0x524F434BUL));
            if (tile == 0) continue;
            ir.LandId[i] = tile;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Paints brush edge tiles on every non-road land cell touching the road set. The edge
    /// is owned by the surrounding biome ("Grassland -> Dirt" etc.); the mask is the road
    /// neighbours. Cells whose biome has no brush for the material keep their tile.
    /// Mountain cells are skipped when <paramref name="skipMountain"/> (Cliff Edge lines
    /// trails with cliff faces instead).
    /// </summary>
    public static int PaintEdges(GenIR ir, HashSet<int> road, string material, bool skipMountain)
    {
        if (!ir.Brushes.IsLoaded || ir.LandId is null || ir.Biome is null) return 0;
        var seen = new HashSet<int>();
        int painted = 0;
        foreach (int r in road)
        {
            int rx = r % ir.Width, ry = r / ir.Width;
            foreach (var (dx, dy, _) in DirOffsets)
            {
                int bx = rx + dx, by = ry + dy;
                if ((uint)bx >= ir.Width || (uint)by >= ir.Height) continue;
                int b = ir.Index(bx, by);
                if (road.Contains(b) || !seen.Add(b) || IsWater(ir, b)) continue;
                var biome = (BiomeId)ir.Biome[b];
                if (skipMountain && biome is BiomeId.Mountain or BiomeId.HighMountain) continue;
                var brush = BrushFor(biome);
                if (brush is null) continue;
                byte mask = 0;
                foreach (var (ddx, ddy, bit) in DirOffsets)
                {
                    int nx = bx + ddx, ny = by + ddy;
                    if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) continue;
                    if (road.Contains(ir.Index(nx, ny))) mask |= bit;
                }
                if (mask == 0) continue;
                ushort id = ir.Brushes.PickTransitionTile(brush, material, mask, FieldOps.Hash(bx, by, ir.Seed ^ 0x40ADUL));
                if (id == 0 && material == "Cobble") // e.g. Forest has no Cobble key: use its Dirt edge
                    id = ir.Brushes.PickTransitionTile(brush, "Dirt", mask, FieldOps.Hash(bx, by, ir.Seed ^ 0x40ADUL));
                if (id == 0) continue;
                ir.LandId[b] = id;
                painted++;
            }
        }
        return painted;
    }

    /// <summary>
    /// Smooths a height profile along a polyline: moving average over ±<paramref name="window"/>
    /// cells, then a forward/backward clamp so consecutive cells differ by at most
    /// <paramref name="maxStep"/>. Breaks (consecutive cells not 4/8-adjacent) start a new run.
    /// </summary>
    public static int[] SmoothProfile(GenIR ir, IReadOnlyList<(ushort X, ushort Y)> path, int window, int maxStep)
    {
        var z = ir.Height_Z!;
        int n = path.Count;
        var raw = new int[n];
        for (int i = 0; i < n; i++) raw[i] = z[ir.Index(path[i].X, path[i].Y)];
        var outZ = new int[n];
        int start = 0;
        for (int i = 1; i <= n; i++)
        {
            bool brk = i == n || Math.Abs(path[i].X - path[i - 1].X) > 1 || Math.Abs(path[i].Y - path[i - 1].Y) > 1;
            if (!brk) continue;
            for (int k = start; k < i; k++)
            {
                int lo = Math.Max(start, k - window), hi = Math.Min(i - 1, k + window);
                long sum = 0;
                for (int j = lo; j <= hi; j++) sum += raw[j];
                outZ[k] = (int)Math.Round((double)sum / (hi - lo + 1));
            }
            for (int k = start + 1; k < i; k++) outZ[k] = Math.Clamp(outZ[k], outZ[k - 1] - maxStep, outZ[k - 1] + maxStep);
            for (int k = i - 2; k >= start; k--) outZ[k] = Math.Clamp(outZ[k], outZ[k + 1] - maxStep, outZ[k + 1] + maxStep);
            start = i;
        }
        return outZ;
    }
}
