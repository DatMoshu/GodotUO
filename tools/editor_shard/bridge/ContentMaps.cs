// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace GUO.EditorBridge;

internal sealed class ContentMaps
{
    private sealed record Block(Map Map, int X, int Y, LandTile[] Land, StaticTile[][][] Statics);
    private readonly List<Block> _blocks = new();

    public static ContentMaps Stage(JsonElement root)
    {
        var result = new ContentMaps();
        if (!root.TryGetProperty("maps", out var maps)) return result;
        var seen = new HashSet<(int, int, int)>();
        foreach (var component in maps.EnumerateArray())
        {
            int facet = component.GetProperty("facet").GetInt32();
            if (facet < 0 || facet >= Map.Maps.Length || Map.Maps[facet] == null || Map.Maps[facet] == Map.Internal)
                throw new InvalidDataException("Unknown server map facet");
            var map = Map.Maps[facet];
            var content = component.GetProperty("content");
            GUO.Store.StoreMapDefinition.Validate(content);
            foreach (var row in content.GetProperty("blocks").EnumerateArray())
            {
                int x = row.GetProperty("x").GetInt32(), y = row.GetProperty("y").GetInt32();
                if (x >= map.Tiles.BlockWidth || y >= map.Tiles.BlockHeight || !seen.Add((facet, x, y)) || seen.Count > 65536)
                    throw new InvalidDataException("Conflicting or out-of-bounds server map block");
                var land = new LandTile[64];
                int index = 0;
                foreach (var cell in row.GetProperty("land").EnumerateArray())
                    land[index++] = new LandTile(cell.GetProperty("graphic").GetInt16(), cell.GetProperty("z").GetSByte());
                StaticTile[][][] statics = null;
                if (row.TryGetProperty("statics", out var items))
                {
                    var cells = new List<StaticTile>[8, 8];
                    foreach (var item in items.EnumerateArray())
                    {
                        byte sx = item.GetProperty("x").GetByte(), sy = item.GetProperty("y").GetByte();
                        (cells[sx, sy] ??= new()).Add(new StaticTile(item.GetProperty("graphic").GetUInt16(), sx, sy,
                            item.GetProperty("z").GetSByte(), item.GetProperty("hue").GetInt16()));
                    }
                    statics = new StaticTile[8][][];
                    for (int sx = 0; sx < 8; sx++)
                    {
                        statics[sx] = new StaticTile[8][];
                        for (int sy = 0; sy < 8; sy++) statics[sx][sy] = cells[sx, sy]?.ToArray() ?? Array.Empty<StaticTile>();
                    }
                }
                result._blocks.Add(new Block(map, x, y, land, statics));
            }
        }
        return result;
    }

    public void Apply(bool probe)
    {
        foreach (var block in _blocks)
        {
            block.Map.Tiles.SetLandBlock(block.X, block.Y, block.Land);
            if (block.Statics != null) block.Map.Tiles.SetStaticBlock(block.X, block.Y, block.Statics);
        }
        Console.WriteLine($"[GUO content] Applied {_blocks.Count} authored server map blocks.");
        if (!probe || _blocks.Count == 0) return;
        int collisionChecks = 0;
        foreach (var block in _blocks)
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                var actual = block.Map.Tiles.GetLandTile(block.X * 8 + x, block.Y * 8 + y);
                var expected = block.Land[y * 8 + x];
                if (actual.ID != expected.ID || actual.Z != expected.Z) throw new InvalidDataException("Server terrain probe differs");
                if (block.Statics == null) continue;
                var found = block.Map.Tiles.GetStaticBlock(block.X, block.Y)[x][y];
                var wanted = block.Statics[x][y];
                if (found.Length != wanted.Length) throw new InvalidDataException("Server static count differs");
                for (int i = 0; i < wanted.Length; i++)
                {
                    if (found[i].ID != wanted[i].ID || found[i].Z != wanted[i].Z || found[i].Hue != wanted[i].Hue)
                        throw new InvalidDataException("Server static data differs");
                    var metadata = TileData.ItemTable[wanted[i].ID];
                    if (metadata.Impassable && metadata.Height > 0)
                    {
                        collisionChecks++;
                        if (block.Map.CanFit(block.X * 8 + x, block.Y * 8 + y, wanted[i].Z, 16, false, false, false))
                            throw new InvalidDataException("Authored impassable static did not block CanFit");
                    }
                }
            }
        Console.WriteLine("[GUO content] PASS: collision tile matrix reads every authored land/static cell unchanged.");
        Console.WriteLine($"[GUO content] PASS: {collisionChecks} impassable static CanFit rejection checks.");
    }
}
