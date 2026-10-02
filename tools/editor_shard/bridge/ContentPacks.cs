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
    private static ContentLoot Loot;

    /// <summary>Generate authored loot on the server game thread. The caller owns the returned items.</summary>
    public static List<Item> GenerateLoot(string identity) =>
        (Loot ?? throw new InvalidOperationException("No content deployment loaded")).Generate(identity, CreateItem, Utility.RandomDouble);

    private static Item CreateItem(string identity) => Create(Items[identity]);

    public static void Initialize()
    {
        // UO_SERVER_CONTENT names an export for this run; otherwise a deployed shard keeps it in
        // Data/GUO/server-content.json, where tools/shard_content deploy puts it (ADR-0026 section 5).
        string path = Environment.GetEnvironmentVariable("UO_SERVER_CONTENT");
        if (string.IsNullOrWhiteSpace(path))
        {
            string deployed = Path.Combine(Core.BaseDirectory, "Data", "GUO", "server-content.json");
            if (File.Exists(deployed)) path = deployed;
        }
        if (string.IsNullOrWhiteSpace(path)) { new ContentDecorations().Register(false); ContentCreatures.RegisterPersistenceProbe(null); return; }
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
        var maps = ContentMaps.Stage(doc.RootElement);
        var tiles = ContentTiles.Stage(doc.RootElement);
        var regions = ContentRegions.Stage(doc.RootElement);
        var decorations = ContentDecorations.Stage(doc.RootElement);
        var loot = ContentLoot.Stage(doc.RootElement, staged.ContainsKey);
        var creatures = ContentCreatures.Stage(doc.RootElement);
        ContentTiles.Apply(tiles, Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        maps.Apply(Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        regions.Apply(Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        decorations.Register(Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        foreach (var pair in staged) Items.Add(pair.Key, pair.Value);
        Loot = loot;
        loot.Register(CreateItem, Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        creatures.Register(Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1");
        CommandSystem.Register("GUOPackItem", AccessLevel.GameMaster, Give);
        string deployment = doc.RootElement.TryGetProperty("identity_hash", out var hash) ? hash.GetString() : null;
        Console.WriteLine($"[GUO content] Loaded {Items.Count} item definitions; no world objects created. Deployment {deployment ?? "(unnamed)"}");
        if (Items.Count > 0 && Environment.GetEnvironmentVariable("UO_SERVER_CONTENT_PROBE") == "1")
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
