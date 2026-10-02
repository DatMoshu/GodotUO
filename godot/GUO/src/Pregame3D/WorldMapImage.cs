// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.IO;
using GUO.Utility;

namespace GUO.Pregame3D;

/// <summary>
/// A small map of a region of a facet, drawn at runtime from the player's own
/// map files with the radar colours (as WorldMapGump draws its map, but land
/// only and sampled down to the size asked for), so no map art is shipped.
/// Built off the main thread.
/// </summary>
internal static class WorldMapImage
{
    /// <summary>
    /// The region <paramref name="tiles"/> of facet <paramref name="map"/>,
    /// <paramref name="size"/> pixels; null when the map cannot be read.
    /// </summary>
    public static Task<Image> Build(int map, Rect2I tiles, Vector2I size) => Task.Run(() =>
    {
        try
        {
            return Draw(map, tiles, size);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: map {map} could not be drawn: {ex.Message}");
            return null;
        }
    });

    private static Image Draw(int map, Rect2I tiles, Vector2I size)
    {
        MapLoader maps = Client.Game.UO.FileManager.Maps;
        HuesLoader hues = Client.Game.UO.FileManager.Hues;
        maps.SanitizeMapIndex(ref map);

        if (maps.BlockData[map] == null)
        {
            lock (maps)
            {
                maps.LoadMap(map);
            }
        }

        int blocksW = maps.MapBlocksSize[map, 0], blocksH = maps.MapBlocksSize[map, 1];
        var files = new Dictionary<string, UOFile>();
        var blocks = new Dictionary<int, MapBlock>();
        var rgba = new byte[size.X * size.Y * 4];
        var z = new sbyte[size.X * size.Y];
        float sx = tiles.Size.X / (float) size.X, sy = tiles.Size.Y / (float) size.Y;

        try
        {
            for (int py = 0; py < size.Y; py++)
            {
                for (int px = 0; px < size.X; px++)
                {
                    int tx = tiles.Position.X + (int) (px * sx), ty = tiles.Position.Y + (int) (py * sy);
                    int bx = tx >> 3, by = ty >> 3;

                    if (bx < 0 || by < 0 || bx >= blocksW || by >= blocksH)
                    {
                        continue;
                    }

                    int blockIndex = bx * blocksH + by;

                    if (!blocks.TryGetValue(blockIndex, out MapBlock block))
                    {
                        ref IndexMap index = ref maps.GetIndex(map, bx, by);

                        if (!index.IsValid())
                        {
                            continue;
                        }

                        string path = index.MapFile.FilePath;

                        if (!files.TryGetValue(path, out UOFile file))
                        {
                            files[path] = file = new UOFile(path);
                        }

                        file.Seek((long) index.MapAddress, System.IO.SeekOrigin.Begin);
                        block = file.Read<MapBlock>();
                        blocks[blockIndex] = block;
                    }

                    var cell = block.Cells[((ty & 7) << 3) + (tx & 7)];
                    ushort c16 = (ushort) (0x8000 | hues.GetRadarColorData(cell.TileID & 0x3FFF));
                    uint c = HuesHelper.Color16To32(c16);
                    int o = (py * size.X + px) * 4;
                    rgba[o] = (byte) (c & 0xFF);
                    rgba[o + 1] = (byte) ((c >> 8) & 0xFF);
                    rgba[o + 2] = (byte) ((c >> 16) & 0xFF);
                    rgba[o + 3] = 255;
                    z[py * size.X + px] = cell.Z;
                }
            }
        }
        finally
        {
            foreach (UOFile f in files.Values)
            {
                f.Dispose();
            }
        }

        // Relief, as the world map shades it: lighter facing up-left, darker away.
        for (int py = 1; py < size.Y; py++)
        {
            for (int px = 1; px < size.X; px++)
            {
                int i = py * size.X + px;
                int d = z[i] - z[i - size.X - 1];

                if (d == 0)
                {
                    continue;
                }

                float f = d > 0 ? 1.15f : 0.85f;
                int o = i * 4;

                for (int k = 0; k < 3; k++)
                {
                    rgba[o + k] = (byte) Math.Clamp(rgba[o + k] * f, 0, 255);
                }
            }
        }

        return Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, rgba);
    }
}
