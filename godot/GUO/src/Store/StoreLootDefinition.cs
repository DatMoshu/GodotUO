// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreLootDefinition
{
    public static void Validate(JsonElement root, Func<string, bool> itemExists)
    {
        Fields(root, "entries");
        var entries = root.GetProperty("entries");
        Require(entries.GetArrayLength() is > 0 and <= 64, "Invalid loot entry count");
        int budget = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            Fields(entry, "item", "chance", "min", "max");
            string item = entry.GetProperty("item").GetString();
            double chance = entry.GetProperty("chance").GetDouble();
            int min = entry.GetProperty("min").GetInt32(), max = entry.GetProperty("max").GetInt32();
            Require(item != null && itemExists(item), "Loot must reference a declared server item component");
            Require(double.IsFinite(chance) && chance >= 0 && chance <= 1 && min >= 1 && max >= min && max <= 256,
                "Invalid loot chance or quantity");
            budget += max;
            Require(budget <= 256, "Loot table exceeds 256 generated items");
        }
    }

    private static void Fields(JsonElement row, params string[] fields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
            Require(Array.IndexOf(fields, property.Name) >= 0 && seen.Add(property.Name), "Unknown or duplicate loot field");
        Require(seen.Count == fields.Length, "Missing loot field");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
