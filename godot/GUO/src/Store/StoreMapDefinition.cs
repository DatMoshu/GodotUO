// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

// Shared by the client, exporter and ModernUO adapter. No engine dependencies.
internal static class StoreMapDefinition
{
    public static void Validate(JsonElement content)
    {
        var blocks = content.GetProperty("blocks");
        Require(blocks.GetArrayLength() is > 0 and <= 65536, "Invalid map block count");
        var seen = new HashSet<(int, int)>();
        long bytes = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            int x = block.GetProperty("x").GetInt32(), y = block.GetProperty("y").GetInt32();
            Require(x is >= 0 and <= 8191 && y is >= 0 and <= 8191 && seen.Add((x, y)), "Invalid or duplicate map block coordinates");
            var land = block.GetProperty("land");
            Require(land.GetArrayLength() == 64, "Map block requires 64 row-major cells");
            foreach (var cell in land.EnumerateArray())
            {
                Require(cell.GetProperty("graphic").GetUInt16() <= 0x3fff, "Invalid land graphic");
                _ = cell.GetProperty("z").GetSByte();
            }
            bytes += 196;
            if (block.TryGetProperty("statics", out var statics))
            {
                Require(statics.GetArrayLength() <= 1024, "Too many map statics");
                bytes += statics.GetArrayLength() * 7;
                foreach (var item in statics.EnumerateArray())
                {
                    Require(item.GetProperty("graphic").GetUInt16() is > 0 and < 0xffff
                        && item.GetProperty("x").GetByte() < 8 && item.GetProperty("y").GetByte() < 8
                        && item.GetProperty("hue").GetUInt16() <= 0x3fff, "Invalid map static");
                    _ = item.GetProperty("z").GetSByte();
                }
            }
            Require(bytes <= 64 * 1024 * 1024, "Map data exceeds memory budget");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
