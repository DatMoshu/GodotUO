// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace GUO.EditorBridge;

internal static class ContentTiles
{
    public static Dictionary<int, ItemData> Stage(JsonElement root)
    {
        var result = new Dictionary<int, ItemData>();
        if (!root.TryGetProperty("tiles", out var tiles)) return result;
        foreach (var entry in tiles.EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();
            if (id < 0 || id >= TileData.ItemTable.Length) throw new InvalidDataException("Tile binding exceeds server capacity");
            var row = entry.GetProperty("content");
            GUO.Store.StoreTileDefinition.Validate(row);
            var tile = TileData.ItemTable[id];
            if (row.TryGetProperty("name", out var value)) tile.Name = value.GetString();
            if (row.TryGetProperty("flags", out value)) tile.Flags = (TileFlag)value.GetUInt64();
            if (row.TryGetProperty("height", out value)) tile.Height = value.GetByte();
            if (row.TryGetProperty("weight", out value)) tile.Weight = value.GetByte();
            if (row.TryGetProperty("layer", out value)) tile.Quality = value.GetByte();
            if (row.TryGetProperty("animation", out value)) tile.Animation = value.GetUInt16();
            if (!result.TryAdd(id, tile)) throw new InvalidDataException("Conflicting server tile definitions");
        }
        return result;
    }

    public static void Apply(Dictionary<int, ItemData> tiles, bool probe)
    {
        foreach (var (id, tile) in tiles) TileData.ItemTable[id] = tile;
        if (!probe || tiles.Count == 0) return;
        foreach (var (id, expected) in tiles)
        {
            var actual = TileData.ItemTable[id];
            var placed = new StaticTile((ushort)id, 0);
            if (actual.Flags != expected.Flags || actual.Height != expected.Height || placed.Height != expected.Height
                || actual.Name != expected.Name || actual.Weight != expected.Weight || actual.Animation != expected.Animation
                || actual.Quality != expected.Quality) throw new InvalidDataException("Server tile metadata probe differs");
        }
        Console.WriteLine($"[GUO content] PASS: {tiles.Count} shared tile definitions match server flags, height and static collision metadata.");
    }
}
