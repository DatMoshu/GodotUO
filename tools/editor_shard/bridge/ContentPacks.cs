// SPDX-License-Identifier: BSD-2-Clause
// Declarative content adapter. Packs never supply executable server code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;

namespace GUO.EditorBridge;

public static class ContentPacks
{
    private sealed record ItemDefinition(int Graphic, string Name, double Weight, bool Movable);
    private static readonly Dictionary<string, ItemDefinition> Items = new(StringComparer.Ordinal);

    public static void Initialize()
    {
        string path = Environment.GetEnvironmentVariable("UO_SERVER_CONTENT");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Server content exceeds limit");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.GetProperty("schema").GetString() != "guo/server-content@1") throw new InvalidDataException("Unsupported server content");
        var staged = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string identity = row.GetProperty("identity").GetString(), name = row.GetProperty("name").GetString();
            int graphic = row.GetProperty("graphic").GetInt32();
            double weight = row.GetProperty("weight").GetDouble();
            if (identity == null || identity.Length > 129 || name == null || name.Length > 100 || graphic < 0 || graphic > 65535 || !double.IsFinite(weight) || weight < 0 || weight > 100000)
                throw new InvalidDataException("Invalid server item definition");
            staged.Add(identity, new ItemDefinition(graphic, name, weight, row.GetProperty("movable").GetBoolean()));
        }
        foreach (var pair in staged) Items.Add(pair.Key, pair.Value);
        CommandSystem.Register("GUOPackItem", AccessLevel.GameMaster, Give);
        Console.WriteLine($"[GUO content] Loaded {Items.Count} item definitions; no world objects created.");
        if (Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1")
        {
            foreach (var definition in Items.Values)
            {
                var item = Create(definition);
                bool valid = item.ItemID == definition.Graphic && item.Name == definition.Name && item.Weight == definition.Weight && item.Movable == definition.Movable;
                item.Delete();
                if (!valid || !item.Deleted) throw new InvalidDataException("Server item probe failed");
            }
            Console.WriteLine("[GUO content] PASS: created and removed every declared item with matching graphic/name/weight/movable.");
        }
    }

    private static Item Create(ItemDefinition definition) => new Item(definition.Graphic) { Name = definition.Name, Weight = definition.Weight, Movable = definition.Movable };

    private static void Give(CommandEventArgs args)
    {
        if (args.Length != 1 || !Items.TryGetValue(args.GetString(0), out var definition))
        {
            args.Mobile.SendMessage("Usage: GUOPackItem pack-id:component-id (installed item definition)");
            return;
        }
        var item = Create(definition);
        args.Mobile.AddToBackpack(item);
        args.Mobile.SendMessage("Created " + definition.Name);
    }
}
