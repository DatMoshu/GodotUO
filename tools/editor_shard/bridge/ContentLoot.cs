// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GUO.Store;
using Server;
using Server.Commands;

namespace GUO.EditorBridge;

internal sealed class ContentLoot
{
    private sealed record Entry(string Item, double Chance, int Min, int Max);
    private readonly Dictionary<string, Entry[]> _tables = new(StringComparer.Ordinal);

    public static ContentLoot Stage(JsonElement root, Func<string, bool> itemExists)
    {
        var result = new ContentLoot();
        if (!root.TryGetProperty("loot", out var tables)) return result;
        if (tables.GetArrayLength() > 256) throw new InvalidDataException("Too many loot tables");
        foreach (var table in tables.EnumerateArray())
        {
            string identity = table.GetProperty("identity").GetString();
            if (identity == null || !System.Text.RegularExpressions.Regex.IsMatch(identity, @"\A[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}\z"))
                throw new InvalidDataException("Invalid loot identity");
            var content = table.GetProperty("content");
            StoreLootDefinition.Validate(content, itemExists);
            result._tables.Add(identity, content.GetProperty("entries").EnumerateArray().Select(e => new Entry(
                e.GetProperty("item").GetString(), e.GetProperty("chance").GetDouble(),
                e.GetProperty("min").GetInt32(), e.GetProperty("max").GetInt32())).ToArray());
        }
        return result;
    }

    // Caller owns returned items. Allocate individual items: authored definitions do not
    // promise stackability. Any failure removes every item already created by this roll.
    public List<Item> Generate(string identity, Func<string, Item> create, Func<double> random)
    {
        if (!_tables.TryGetValue(identity, out var entries)) throw new ArgumentException("Unknown loot table", nameof(identity));
        var items = new List<Item>();
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Chance == 0 || Roll(random) >= entry.Chance) continue;
                int count = entry.Min + (int)(Roll(random) * (entry.Max - entry.Min + 1));
                for (int i = 0; i < count; i++) items.Add(create(entry.Item));
            }
            return items;
        }
        catch { foreach (var item in items) item.Delete(); throw; }
    }

    private static double Roll(Func<double> random)
    {
        double value = random();
        if (!double.IsFinite(value) || value < 0 || value >= 1) throw new InvalidDataException("Loot RNG must return [0,1)");
        return value;
    }

    public void Register(Func<string, Item> create, bool probe)
    {
        CommandSystem.Register("GUOPackLoot", AccessLevel.GameMaster, args =>
        {
            if (args.Length != 1 || !_tables.ContainsKey(args.GetString(0)))
            { args.Mobile.SendMessage("Usage: GUOPackLoot pack-id:component-id"); return; }
            var items = Generate(args.GetString(0), create, Utility.RandomDouble);
            var bag = new Server.Items.Bag();
            try
            {
                foreach (var item in items) bag.DropItem(item);
                args.Mobile.AddToBackpack(bag);
                args.Mobile.SendMessage($"Generated {items.Count} loot items.");
            }
            catch { foreach (var item in items) item.Delete(); bag.Delete(); throw; }
        });
        if (!probe) return;
        foreach (var (identity, entries) in _tables)
        {
            foreach (double roll in new[] { 0.0, 0.999999999999 })
            {
                var items = Generate(identity, create, () => roll);
                try
                {
                    int expected = entries.Where(e => e.Chance > roll).Sum(e => roll == 0 ? e.Min : e.Max);
                    if (items.Count != expected || items.Any(i => i == null || i.Deleted))
                        throw new InvalidDataException("Loot probability/quantity boundary probe failed");
                }
                finally { foreach (var item in items) item.Delete(); }
            }
            if (entries.Where(e => e.Chance > 0).Sum(e => e.Min) >= 2)
            {
                Item first = null;
                bool rejected = false;
                try
                {
                    Generate(identity, id =>
                    {
                        if (first != null) throw new InvalidOperationException("Injected item creation failure");
                        return first = create(id);
                    }, () => 0);
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || first == null || !first.Deleted) throw new InvalidDataException("Failed loot roll leaked items");
            }
        }
        Console.WriteLine($"[GUO content] PASS: {_tables.Count} loot tables generated at probability/quantity boundaries and cleaned up.");
    }
}
