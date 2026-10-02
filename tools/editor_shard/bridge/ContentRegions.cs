// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace GUO.EditorBridge;

internal sealed class ContentRegions
{
    private sealed class PackRegion : Region
    {
        private readonly string _enter, _exit;
        public PackRegion(string name, Map map, int priority, Rectangle3D[] areas, string enter, string exit)
            : base(name, map, priority, areas) { _enter = enter; _exit = exit; }
        public override void OnEnter(Mobile mobile)
        {
            base.OnEnter(mobile);
            if (mobile.Player && !string.IsNullOrEmpty(_enter)) mobile.SendMessage(_enter);
        }
        public override void OnExit(Mobile mobile)
        {
            base.OnExit(mobile);
            if (mobile.Player && !string.IsNullOrEmpty(_exit)) mobile.SendMessage(_exit);
        }
    }
    private readonly List<PackRegion> _regions = new();

    public static ContentRegions Stage(JsonElement root)
    {
        var result = new ContentRegions();
        if (!root.TryGetProperty("regions", out var regions)) return result;
        if (regions.GetArrayLength() > 128) throw new InvalidDataException("Too many authored regions");
        var names = new HashSet<(int, string)>();
        foreach (var entry in regions.EnumerateArray())
        {
            var row = entry.GetProperty("content");
            GUO.Store.StoreRegionDefinition.Validate(row);
            int facet = row.GetProperty("facet").GetInt32();
            var map = Map.Maps[facet];
            if (map == null || map == Map.Internal) throw new InvalidDataException("Unknown region facet");
            string name = row.GetProperty("name").GetString();
            if (!names.Add((facet, name)) || Region.Find(name, map) != null) throw new InvalidDataException("Region name already exists on facet");
            var areas = new List<Rectangle3D>();
            foreach (var area in row.GetProperty("areas").EnumerateArray())
            {
                int x = area.GetProperty("x").GetInt32(), y = area.GetProperty("y").GetInt32();
                int width = area.GetProperty("width").GetInt32(), height = area.GetProperty("height").GetInt32();
                if (x + width > map.Width || y + height > map.Height) throw new InvalidDataException("Region exceeds facet bounds");
                areas.Add(new Rectangle3D(x, y, area.GetProperty("z").GetInt32(), width, height, area.GetProperty("depth").GetInt32()));
            }
            var region = new PackRegion(name, map, row.GetProperty("priority").GetInt32(), areas.ToArray(),
                row.TryGetProperty("enter_message", out var enter) ? enter.GetString() : null,
                row.TryGetProperty("exit_message", out var exit) ? exit.GetString() : null);
            int music = entry.GetProperty("music_id").GetInt32();
            if (music < -1 || music > 65534) throw new InvalidDataException("Invalid region music ID");
            region.Music = (MusicName)music;
            region.GoLocation = areas[0].Start;
            result._regions.Add(region);
        }
        return result;
    }

    public void Apply(bool probe)
    {
        try
        {
            foreach (var region in _regions) region.Register();
            if (!probe || _regions.Count == 0) return;
            foreach (var region in _regions)
            {
                if (!region.Registered || Region.Find(region.Name, region.Map) != region)
                    throw new InvalidDataException("Authored region was not registered");
                foreach (var area in region.Area)
                    if (!region.Contains(area.Start)) throw new InvalidDataException("Authored region does not contain its start point");
            }
            Console.WriteLine($"[GUO content] PASS: {_regions.Count} authored regions registered with matching bounds and lookup.");
        }
        catch
        {
            foreach (var region in _regions) if (region.Registered) region.Unregister();
            throw;
        }
    }
}
