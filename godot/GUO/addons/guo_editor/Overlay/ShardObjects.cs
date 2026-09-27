#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>A spawner in the neutral model (ADR-0014): server-neutral fields plus an opaque extra bag.</summary>
public sealed class ShardSpawner
{
    public Guid Id = Guid.NewGuid();
    public string Map = "Felucca";
    public int X, Y, Z;
    public int Count = 1;
    public string MinDelay = "00:05:00";
    public string MaxDelay = "00:10:00";
    public int HomeRange = 2;
    public int WalkingRange = -1;
    public int Team;
    public List<(string Name, int Max, int Probability)> Entries = new();
    public JsonObject Extra = new();
}

/// <summary>A placed item (decoration) in the neutral model.</summary>
public sealed class ShardItem
{
    public Guid Id = Guid.NewGuid();
    public string Map = "Felucca";
    public int X, Y, Z;
    public ushort ItemId;
    public ushort Hue;
    public string Type = "Static";
    public JsonObject Props = new();
    public JsonObject Extra = new();
}

/// <summary>
/// The world objects of a world project (docs/editor_plan.md phase 6,
/// ADR-0014): spawners and placed items, in <c>&lt;project&gt;/shard/objects.json</c>.
/// </summary>
/// <remarks>
/// Server-neutral: no backend's file format appears here; tools/world's
/// backend adapters write those at export. Written by hand, one object per
/// line, sorted by map, y, x, id, so an edit reviews as the lines it changed.
/// The file is the save: every change writes it.
/// </remarks>
public sealed class ShardObjects
{
    public const int Format = 1;

    /// <summary>Facet index to the neutral map name, as ModernUO, ServUO and RunUO name them.</summary>
    public static readonly string[] MapNames = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };

    public static int FacetOf(string map) => Array.FindIndex(MapNames, n => n.Equals(map, StringComparison.OrdinalIgnoreCase));

    public string Path { get; }
    public List<ShardSpawner> Spawners { get; } = new();
    public List<ShardItem> Items { get; } = new();

    public ShardObjects(string projectRoot)
    {
        Path = System.IO.Path.Combine(projectRoot, "shard", "objects.json");
        if (File.Exists(Path))
        {
            Load(File.ReadAllText(Path));
        }
    }

    public int Count => Spawners.Count + Items.Count;

    private void Load(string text)
    {
        JsonNode j = JsonNode.Parse(text);
        foreach (JsonNode s in j["spawners"]?.AsArray() ?? new JsonArray())
        {
            var sp = new ShardSpawner
            {
                Id = Guid.Parse((string)s["id"]),
                Map = (string)s["map"],
                X = (int)s["x"],
                Y = (int)s["y"],
                Z = (int)s["z"],
                Count = (int?)s["count"] ?? 1,
                MinDelay = (string)s["min_delay"] ?? "00:05:00",
                MaxDelay = (string)s["max_delay"] ?? "00:10:00",
                HomeRange = (int?)s["home_range"] ?? 2,
                WalkingRange = (int?)s["walking_range"] ?? -1,
                Team = (int?)s["team"] ?? 0,
                Extra = s["extra"]?.DeepClone().AsObject() ?? new JsonObject(),
            };
            foreach (JsonNode e in s["entries"]?.AsArray() ?? new JsonArray())
            {
                sp.Entries.Add(((string)e["name"], (int?)e["max"] ?? 1, (int?)e["probability"] ?? 100));
            }

            Spawners.Add(sp);
        }

        foreach (JsonNode i in j["items"]?.AsArray() ?? new JsonArray())
        {
            Items.Add(new ShardItem
            {
                Id = Guid.Parse((string)i["id"]),
                Map = (string)i["map"],
                X = (int)i["x"],
                Y = (int)i["y"],
                Z = (int)i["z"],
                ItemId = Hex((string)i["item_id"]),
                Hue = Hex((string)i["hue"] ?? "0x0000"),
                Type = (string)i["type"] ?? "Static",
                Props = i["props"]?.DeepClone().AsObject() ?? new JsonObject(),
                Extra = i["extra"]?.DeepClone().AsObject() ?? new JsonObject(),
            });
        }
    }

    private static ushort Hex(string s) =>
        ushort.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Writes the file; an empty model removes it.</summary>
    public void Save()
    {
        if (Count == 0)
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return;
        }

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"format\": {Format},\n");
        sb.Append("  \"spawners\": [");
        var spawners = Spawners.OrderBy(s => s.Map).ThenBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Id).ToList();
        for (int n = 0; n < spawners.Count; n++)
        {
            ShardSpawner s = spawners[n];
            var o = new JsonObject
            {
                ["id"] = s.Id.ToString(),
                ["map"] = s.Map,
                ["x"] = s.X,
                ["y"] = s.Y,
                ["z"] = s.Z,
                ["count"] = s.Count,
                ["min_delay"] = s.MinDelay,
                ["max_delay"] = s.MaxDelay,
                ["home_range"] = s.HomeRange,
                ["walking_range"] = s.WalkingRange,
                ["team"] = s.Team,
                ["entries"] = new JsonArray(s.Entries.Select(e => (JsonNode)new JsonObject
                {
                    ["name"] = e.Name,
                    ["max"] = e.Max,
                    ["probability"] = e.Probability,
                }).ToArray()),
                ["extra"] = s.Extra.DeepClone(),
            };
            sb.Append(n == 0 ? "\n" : ",\n").Append("    ").Append(o.ToJsonString());
        }

        sb.Append(spawners.Count > 0 ? "\n  ],\n" : "],\n");
        sb.Append("  \"items\": [");
        var items = Items.OrderBy(i => i.Map).ThenBy(i => i.Y).ThenBy(i => i.X).ThenBy(i => i.Id).ToList();
        for (int n = 0; n < items.Count; n++)
        {
            ShardItem i = items[n];
            var o = new JsonObject
            {
                ["id"] = i.Id.ToString(),
                ["map"] = i.Map,
                ["x"] = i.X,
                ["y"] = i.Y,
                ["z"] = i.Z,
                ["item_id"] = $"0x{i.ItemId:X4}",
                ["hue"] = $"0x{i.Hue:X4}",
                ["type"] = i.Type,
                ["props"] = i.Props.DeepClone(),
                ["extra"] = i.Extra.DeepClone(),
            };
            sb.Append(n == 0 ? "\n" : ",\n").Append("    ").Append(o.ToJsonString());
        }

        sb.Append(items.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        File.WriteAllText(Path, sb.ToString());
    }

    /// <summary>The object at a cell on a facet (spawners first), or null.</summary>
    public object At(int facet, int x, int y)
    {
        string map = MapNames[facet];
        return (object)Spawners.FirstOrDefault(s => s.Map == map && s.X == x && s.Y == y)
               ?? Items.FirstOrDefault(i => i.Map == map && i.X == x && i.Y == y);
    }

    public object Find(Guid id) =>
        (object)Spawners.FirstOrDefault(s => s.Id == id) ?? Items.FirstOrDefault(i => i.Id == id);

    public bool Remove(Guid id) =>
        Spawners.RemoveAll(s => s.Id == id) + Items.RemoveAll(i => i.Id == id) > 0;
}
#endif
