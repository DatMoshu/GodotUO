// SPDX-License-Identifier: BSD-2-Clause
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreRegionDefinition
{
    public static void Validate(JsonElement row)
    {
        string name = row.GetProperty("name").GetString();
        int facet = row.GetProperty("facet").GetInt32(), priority = row.GetProperty("priority").GetInt32();
        Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 100 && facet is >= 0 and < 256
            && priority is >= 0 and <= 150, "Invalid region name, facet or priority");
        foreach (string field in new[] { "enter_message", "exit_message" })
            if (row.TryGetProperty(field, out var message)) Require(message.GetString() is { Length: <= 512 }, "Invalid region message");
        var areas = row.GetProperty("areas");
        Require(areas.GetArrayLength() is > 0 and <= 64, "Invalid region area count");
        long areaTotal = 0;
        foreach (var area in areas.EnumerateArray())
        {
            int x = area.GetProperty("x").GetInt32(), y = area.GetProperty("y").GetInt32(), z = area.GetProperty("z").GetInt32();
            int width = area.GetProperty("width").GetInt32(), height = area.GetProperty("height").GetInt32(), depth = area.GetProperty("depth").GetInt32();
            Require(x is >= 0 and <= 65535 && y is >= 0 and <= 65535 && z is >= -128 and <= 127
                && width is > 0 and <= 65535 && height is > 0 and <= 65535 && depth > 0 && depth <= 128 - z,
                "Invalid region bounds");
            areaTotal += (long)width * height;
            Require(areaTotal <= 64 * 1024 * 1024, "Region coverage exceeds budget");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
