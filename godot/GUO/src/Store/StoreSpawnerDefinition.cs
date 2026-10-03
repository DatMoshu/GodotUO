// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreSpawnerDefinition
{
    public static void Validate(JsonElement root, Func<string, bool> creatureExists)
    {
        var expected = new HashSet<string>(new[] { "creature", "facet", "x", "y", "z", "radius", "count", "min_delay", "max_delay", "team" }, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            Require(expected.Remove(field.Name), "Unknown or duplicate spawner field");
        Require(expected.Count == 0, "Missing spawner field");
        Require(root.GetProperty("creature").ValueKind == JsonValueKind.String && root.GetProperty("creature").GetString() is string id && creatureExists(id), "Undeclared spawner creature reference");
        foreach (string key in new[] { "facet", "x", "y", "z", "radius", "count", "min_delay", "max_delay", "team" })
            Require(root.GetProperty(key).ValueKind == JsonValueKind.Number && root.GetProperty(key).TryGetInt32(out _), "Spawner " + key + " must be an integer");
        Range(root, "facet", 0, 255); Range(root, "x", 0, 65535); Range(root, "y", 0, 65535);
        Range(root, "z", -128, 127); Range(root, "radius", 0, 64); Range(root, "count", 1, 64); Range(root, "team", 0, 65535);
        Range(root, "min_delay", 1, 86400); Range(root, "max_delay", root.GetProperty("min_delay").GetInt32(), 86400);
    }
    private static void Range(JsonElement row, string key, int min, int max)
    { int value = row.GetProperty(key).GetInt32(); Require(value >= min && value <= max, "Invalid spawner " + key); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
