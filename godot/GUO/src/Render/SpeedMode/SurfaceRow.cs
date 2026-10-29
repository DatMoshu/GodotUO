// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.InteropServices;
using GUO.Game.GameObjects;
using GUO.Renderer.Lights;

namespace GUO.Renderer.SpeedMode;

// ADR-0046: parallel to ordered occurrences, never added to the colour record.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct SurfaceRow
{
    internal readonly uint Tile, Data;
    internal SurfaceRow(int x, int y, int z, int height, int kind, int flags = 0)
    {
        Tile = (uint)(ushort)x | ((uint)(ushort)y << 16);
        Data = (uint)(kind + 16 * flags) | ((uint)(byte)(z + 128) << 12)
            | ((uint)(byte)Math.Clamp(height, 0, 255) << 20);
    }

    internal SurfaceRow(uint tile, uint data) { Tile = tile; Data = data; }

    internal static SurfaceRow FromMesh(MeshLayer layer, int index)
    {
        uint data = layer.SurfaceData[index];
        int z = (int)((data >> 12) & 255) - 128;
        ref var quad = ref layer.Vertices[index];
        bool rectangle = quad.Position0.Y == quad.Position1.Y;
        float diff = (rectangle ? (quad.Position0.X + quad.Position3.X) * 0.5f : quad.Position0.X) / 22f;
        float sum = MathF.Round(layer.Depths[index] - 0.5f - (127 + z) * 0.01f);
        int x = (int)MathF.Round((sum + diff) * 0.5f), y = (int)MathF.Round((sum - diff) * 0.5f);
        return new SurfaceRow((uint)(ushort)x | ((uint)(ushort)y << 16), data);
    }

    internal static SurfaceRow For(GameObject obj)
    {
        if (obj == null) return default;
        int kind = 4, flags = 0, height = 0;
        if (obj is Land land)
        {
            kind = land.TileData.IsWet ? 6 : 1;
            flags |= 2;
        }
        else if (obj is Static or Multi or Item)
        {
            ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
            if (obj.Graphic < tiles.Length)
            {
                ref var tile = ref tiles[obj.Graphic];
                height = tile.Height;
                kind = tile.IsWet ? 6 : tile.IsRoof ? 5 : tile.IsSurface && height < 4 ? 1 : 4;
                if (tile.IsWall || tile.IsWindow || tile.IsDoor)
                {
                    var faces = WallFaces.For(obj.Graphic);
                    kind = faces.Count > 0 && faces.First.AlongX ? 2 : 3;
                    // Both faces are resolved per fragment around the tile's centre.
                    if (faces.Count == 2) kind = 7;
                }
                if (kind == 1 || kind == 6) flags |= 2;
                if (tile.IsWindow) flags |= 4;
                if (tile.IsTranslucent || tile.IsTransparent) flags |= 8;
            }
        }
        GameObject first = obj;
        while (first.TPrevious != null) first = first.TPrevious;
        for (var above = first; above != null; above = above.TNext)
        {
            if (above.Z <= obj.Z || above is not (Static or Multi or Item)) continue;
            ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
            if (above.Graphic >= tiles.Length) continue;
            ref var tile = ref tiles[above.Graphic];
            if (tile.IsRoof || (tile.IsSurface && tile.Height < 4 && above.Z >= obj.Z + RealtimeLightField.UpperFloorZ))
            { flags |= 1; break; }
        }
        return new SurfaceRow(obj.X, obj.Y, obj.Z, height, kind, flags);
    }
}
