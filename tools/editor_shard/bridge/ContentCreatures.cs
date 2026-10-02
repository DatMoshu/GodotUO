// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GUO.Store;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace GUO.EditorBridge;

internal sealed class ContentCreatures
{
    private readonly Dictionary<string, (JsonElement Content, string Loot)> _definitions = new(StringComparer.Ordinal);

    public static ContentCreatures Stage(JsonElement root)
    {
        var result = new ContentCreatures();
        if (!root.TryGetProperty("creatures", out var creatures)) return result;
        if (creatures.GetArrayLength() > 256) throw new InvalidDataException("Too many creature definitions");
        var loot = root.TryGetProperty("loot", out var tables)
            ? tables.EnumerateArray().ToDictionary(t => t.GetProperty("identity").GetString(), t => t, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in creatures.EnumerateArray())
        {
            string id = row.GetProperty("identity").GetString();
            if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}\z"))
                throw new InvalidDataException("Invalid creature identity");
            var content = row.GetProperty("content");
            StoreCreatureDefinition.Validate(content, loot.ContainsKey);
            string snapshot = "";
            if (content.TryGetProperty("loot", out var reference))
            {
                var table = loot[reference.GetString()];
                var needed = table.GetProperty("content").GetProperty("entries").EnumerateArray()
                    .Select(e => e.GetProperty("item").GetString()).ToHashSet(StringComparer.Ordinal);
                snapshot = JsonSerializer.Serialize(new { loot = new[] { table }, items = root.GetProperty("items").EnumerateArray()
                    .Where(i => needed.Contains(i.GetProperty("identity").GetString())).ToArray() });
            }
            result._definitions.Add(id, (content.Clone(), snapshot));
        }
        return result;
    }

    public ContentCreature Create(string identity)
    {
        if (!_definitions.TryGetValue(identity, out var definition)) throw new ArgumentException("Unknown creature definition", nameof(identity));
        return new ContentCreature(identity, definition.Content, definition.Loot);
    }

    public void Register(bool probe)
    {
        RegisterPersistenceProbe(this);
        CommandSystem.Register("GUOPackCreature", AccessLevel.GameMaster, args =>
        {
            if (args.Length != 1 || !_definitions.ContainsKey(args.GetString(0)))
            { args.Mobile.SendMessage("Usage: GUOPackCreature pack-id:component-id"); return; }
            var creature = Create(args.GetString(0));
            try { creature.MoveToWorld(args.Mobile.Location, args.Mobile.Map); }
            catch { creature.Delete(); throw; }
        });
        if (!probe || _definitions.Count == 0) return;
        // Delay until the shard is fully initialized, using its normal death/corpse lifecycle.
        Server.Timer.DelayCall(TimeSpan.Zero, () =>
        {
            foreach (var (identity, definition) in _definitions)
            {
                var creature = Create(identity);
                Container corpse = null;
                try
                {
                    var row = definition.Content;
                    if (creature.Name != row.GetProperty("name").GetString() || (int)creature.Body != row.GetProperty("body").GetInt32()
                        || creature.Hits != row.GetProperty("hits").GetInt32() || creature.RawStr != row.GetProperty("strength").GetInt32())
                        throw new InvalidDataException("Creature runtime fields differ");
                    creature.MoveToWorld(new Point3D(100, 100, 0), Map.Felucca);
                    creature.Kill();
                    corpse = creature.Corpse;
                    if (!creature.Deleted || corpse == null) throw new InvalidDataException("Creature death did not produce a corpse");
                    if (row.TryGetProperty("loot", out _))
                    {
                        using var doc = JsonDocument.Parse(definition.Loot);
                        var entries = doc.RootElement.GetProperty("loot")[0].GetProperty("content").GetProperty("entries").EnumerateArray().ToArray();
                        int minimum = entries.Where(e => e.GetProperty("chance").GetDouble() == 1).Sum(e => e.GetProperty("min").GetInt32());
                        int maximum = entries.Where(e => e.GetProperty("chance").GetDouble() > 0).Sum(e => e.GetProperty("max").GetInt32());
                        if (corpse.Items.Count < minimum || corpse.Items.Count > maximum) throw new InvalidDataException("Creature corpse loot count differs");
                    }
                }
                finally { corpse?.Delete(); creature.Delete(); }
            }
            Console.WriteLine($"[GUO content] PASS: {_definitions.Count} creatures spawned, died with authored corpse loot, and were cleaned up.");
        });
    }

    internal static void RegisterPersistenceProbe(ContentCreatures definitions)
    {
        string phase = Environment.GetEnvironmentVariable("UO_CREATURE_PERSISTENCE_PROBE");
        if (string.IsNullOrEmpty(phase)) return;
        Server.Timer.DelayCall(TimeSpan.Zero, () =>
        {
            string path = Path.Combine(Core.BaseDirectory, "creature-persistence-probe.json");
            try
            {
                if (phase == "seed")
                {
                    if (File.Exists(path) || definitions == null || definitions._definitions.Count == 0)
                        throw new InvalidDataException("Creature persistence probe requires a fresh record and a deployment");
                    var creature = definitions.Create(definitions._definitions.Keys.First());
                    creature.Frozen = true;
                    creature.MoveToWorld(new Point3D(100, 100, 0), Map.Felucca);
                    File.WriteAllText(path, JsonSerializer.Serialize(new { serial = (uint)creature.Serial, identity = creature.ContentIdentity,
                        name = creature.Name, hits = creature.Hits, loot = creature.LootSnapshot }));
                }
                else
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var record = doc.RootElement;
                    var mobile = World.FindMobile((Serial)record.GetProperty("serial").GetUInt32());
                    if (phase == "verify")
                    {
                        if (mobile is { Deleted: false }) throw new InvalidDataException("Creature cleanup was not saved");
                        File.Delete(path);
                        Console.WriteLine("[GUO creature persistence] PASS verify: saved cleanup reloaded.");
                        return;
                    }
                    if (phase != "reload" || mobile is not ContentCreature creature || creature.ContentIdentity != record.GetProperty("identity").GetString()
                        || creature.Name != record.GetProperty("name").GetString() || creature.Hits != record.GetProperty("hits").GetInt32()
                        || creature.LootSnapshot != record.GetProperty("loot").GetString())
                        throw new InvalidDataException("Creature fields or loot snapshot did not survive restart");
                    Container corpse = null;
                    try
                    {
                        creature.Kill(); corpse = creature.Corpse;
                        if (!creature.Deleted || corpse == null) throw new InvalidDataException("Restored creature failed to die");
                        if (!string.IsNullOrEmpty(creature.LootSnapshot))
                        {
                            using var lootDoc = JsonDocument.Parse(creature.LootSnapshot);
                            var root = lootDoc.RootElement;
                            int minimum = root.GetProperty("loot")[0].GetProperty("content").GetProperty("entries").EnumerateArray()
                                .Where(e => e.GetProperty("chance").GetDouble() == 1).Sum(e => e.GetProperty("min").GetInt32());
                            var graphics = root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("graphic").GetInt32()).ToHashSet();
                            if (corpse.Items.Count < minimum || corpse.Items.Any(i => !graphics.Contains(i.ItemID)))
                                throw new InvalidDataException("Restored creature lost its authored drops without the deployment");
                        }
                    }
                    finally { corpse?.Delete(); creature.Delete(); }
                }
                World.Save();
                Console.WriteLine($"[GUO creature persistence] PASS {phase}: assertions passed; save requested (wait for snapshot write completion).");
            }
            catch (Exception ex) { Console.WriteLine($"[GUO creature persistence] FAIL {phase}: {ex}"); }
        });
    }
}

// Public concrete type and Serial constructor let ModernUO restore existing creatures
// even when no deployment is selected. BaseCreature persists stats, AI and position.
public sealed class ContentCreature : BaseCreature
{
    private string _identity = "", _lootIdentity = "", _lootSnapshot = "";
    public string ContentIdentity => _identity;
    internal string LootSnapshot => _lootSnapshot;
    public ContentCreature(Serial serial) : base(serial) { }

    internal ContentCreature(string identity, JsonElement row, string snapshot)
        : base(row.GetProperty("ai").GetString() == "animal" ? AIType.AI_Animal : AIType.AI_Melee, FightMode.Aggressor)
    {
        _identity = identity; _lootSnapshot = snapshot;
        _lootIdentity = row.TryGetProperty("loot", out var loot) ? loot.GetString() : "";
        Name = row.GetProperty("name").GetString(); Body = row.GetProperty("body").GetInt32();
        Hue = row.GetProperty("hue").GetInt32(); BaseSoundID = row.GetProperty("sound").GetInt32();
        SetStr(row.GetProperty("strength").GetInt32()); SetDex(row.GetProperty("dexterity").GetInt32()); SetInt(row.GetProperty("intelligence").GetInt32());
        SetHits(row.GetProperty("hits").GetInt32()); SetDamage(row.GetProperty("damage_min").GetInt32(), row.GetProperty("damage_max").GetInt32());
        SetDamageType(ResistanceType.Physical, 100);
        SetSkill(SkillName.Tactics, row.GetProperty("tactics").GetDouble()); SetSkill(SkillName.Wrestling, row.GetProperty("wrestling").GetDouble());
        SetSkill(SkillName.MagicResist, row.GetProperty("resist").GetDouble());
        VirtualArmor = row.GetProperty("armor").GetInt32(); Fame = row.GetProperty("fame").GetInt32(); Karma = row.GetProperty("karma").GetInt32();
    }

    public override void GetSpeeds(out double activeSpeed, out double passiveSpeed) { activeSpeed = 0.2; passiveSpeed = 0.4; }
    public override void GenerateLoot(bool spawning)
    {
        if (spawning || string.IsNullOrEmpty(_lootSnapshot)) return;
        using var doc = JsonDocument.Parse(_lootSnapshot);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("identity").GetString(), i => i);
        var table = ContentLoot.Stage(doc.RootElement, items.ContainsKey);
        var generated = table.Generate(_lootIdentity, id =>
        {
            var row = items[id];
            return new Item(row.GetProperty("graphic").GetInt32()) { Name = row.GetProperty("name").GetString(),
                Weight = row.GetProperty("weight").GetDouble(), Movable = row.GetProperty("movable").GetBoolean() };
        }, Utility.RandomDouble);
        try { foreach (var item in generated) PackItem(item); }
        catch { foreach (var item in generated) item.Delete(); throw; }
    }

    public override void Serialize(IGenericWriter writer)
    {
        base.Serialize(writer); writer.WriteEncodedInt(0);
        writer.Write(_identity); writer.Write(_lootIdentity); writer.Write(_lootSnapshot);
    }
    public override void Deserialize(IGenericReader reader)
    {
        base.Deserialize(reader);
        if (reader.ReadEncodedInt() != 0) throw new InvalidDataException("Unsupported content creature save version");
        _identity = reader.ReadString(); _lootIdentity = reader.ReadString(); _lootSnapshot = reader.ReadString();
    }
}
