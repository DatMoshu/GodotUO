// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreCreatureDefinition
{
    public static void Validate(JsonElement root, Func<string, bool> lootExists)
    {
        var allowed = new HashSet<string>(new[] { "name", "body", "hue", "sound", "ai", "strength", "dexterity", "intelligence",
            "hits", "damage_min", "damage_max", "armor", "fame", "karma", "tactics", "wrestling", "resist", "loot" }, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            Require(allowed.Contains(field.Name) && seen.Add(field.Name), "Unknown or duplicate creature field");
        Require(root.GetProperty("name").GetString() is { Length: > 0 and <= 100 }, "Invalid creature name");
        Require(root.GetProperty("ai").GetString() is "animal" or "melee", "Unsupported creature AI");
        Range(root, "body", 1, 65535); Range(root, "hue", 0, 16383); Range(root, "sound", -1, 65535);
        foreach (string key in new[] { "strength", "dexterity", "intelligence", "hits" }) Range(root, key, 1, 100000);
        Range(root, "damage_min", 0, 10000); Range(root, "damage_max", root.GetProperty("damage_min").GetInt32(), 10000);
        Range(root, "armor", 0, 1000); Range(root, "fame", 0, 32000); Range(root, "karma", -32000, 32000);
        foreach (string key in new[] { "tactics", "wrestling", "resist" })
        { double value = root.GetProperty(key).GetDouble(); Require(double.IsFinite(value) && value >= 0 && value <= 120, "Invalid creature skill"); }
        if (root.TryGetProperty("loot", out var loot)) Require(loot.GetString() is string id && lootExists(id), "Undeclared creature loot table");
    }
    private static void Range(JsonElement root, string key, int min, int max)
    { int value = root.GetProperty(key).GetInt32(); Require(value >= min && value <= max, "Invalid creature " + key); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
