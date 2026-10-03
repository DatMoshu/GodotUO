#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GUO.Assets;
using GUO.Game.Data;

/// <summary>One group of palette entries: a style of walls, a floor set, a door set.</summary>
public sealed class PaletteGroup
{
    public string Kind;
    public string Name;
    public List<(ushort Id, string Role)> Items = new();
}

/// <summary>
/// The client's house customization tables (walls.txt, floors.txt, doors.txt, stairs.txt, roof.txt, misc.txt,
/// teleprts.txt, suppinfo.txt), read in place from the user's install through the ported
/// <c>CustomHouse*</c> parsers (ADR-0031). Nothing is copied or stored. The install may lack any of them.
/// </summary>
internal sealed class HouseTables
{
    public readonly List<PaletteGroup> Groups = new();
    public readonly HashSet<ushort> FloorIds = new();
    public readonly HashSet<ushort> DoorIds = new();
    public readonly HashSet<ushort> StairIds = new();
    public readonly HashSet<ushort> RoofIds = new();
    public readonly Dictionary<ushort, CustomHousePlaceInfo> PlaceInfo = new();
    public readonly List<string> Missing = new();

    public bool HasSupport => PlaceInfo.Count > 0;

    public static HouseTables Load(UOFileManager files)
    {
        var t = new HouseTables();
        string Path(string f) => files.GetUOFilePath(f);

        t.ReadCategorised<CustomHouseWall>(Path("walls.txt"), "Walls", (w, g) =>
        {
            string[] roles = { "south 1", "south 2", "south 3", "corner", "east 1", "east 2", "east 3", "post" };
            for (int i = 0; i < CustomHouseWall.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((w.Graphics[i], roles[i]));
            }

            string[] wroles = { "window south", "alt window south", "2nd alt window south", "", "window east", "alt window east", "2nd alt window east", "" };
            for (int i = 0; i < CustomHouseWall.GRAPHICS_COUNT; i++)
            {
                if (wroles[i].Length > 0 && w.WindowGraphics[i] != w.Graphics[i])
                {
                    g.Items.Add((w.WindowGraphics[i], wroles[i]));
                }
            }
        }, w => w.TID, w => w.Category, files);

        t.ReadCategorised<CustomHouseFloor>(Path("floors.txt"), "Floors", (f, g) =>
        {
            for (int i = 0; i < CustomHouseFloor.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((f.Graphics[i], $"floor {i + 1}"));
                t.FloorIds.Add(f.Graphics[i]);
            }
        }, null, f => f.Category, files);

        t.ReadCategorised<CustomHouseDoor>(Path("doors.txt"), "Doors", (d, g) =>
        {
            for (int i = 0; i < CustomHouseDoor.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((d.Graphics[i], $"door {i + 1}"));
                t.DoorIds.Add(d.Graphics[i]);
            }
        }, null, d => d.Category, files);

        t.ReadCategorised<CustomHouseStair>(Path("stairs.txt"), "Stairs", (s, g) =>
        {
            string[] roles = { "squared 1", "squared 2", "rounded 1", "rounded 2", "block", "north", "east", "south", "west" };
            for (int i = 0; i < Math.Min(roles.Length, s.Graphics.Length); i++)
            {
                if (s.Graphics[i] != 0)
                {
                    g.Items.Add((s.Graphics[i], roles[i]));
                    t.StairIds.Add(s.Graphics[i]);
                }
            }
        }, null, s => s.Category, files);

        t.ReadCategorised<CustomHouseRoof>(Path("roof.txt"), "Roofs", (r, g) =>
        {
            string[] roles = { "north", "east", "south", "west", "NS cross", "EW cross", "N dent", "E dent", "S dent", "W dent", "N T", "E T", "S T", "W T", "X", "extra" };
            for (int i = 0; i < CustomHouseRoof.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((r.Graphics[i], roles[i]));
                t.RoofIds.Add(r.Graphics[i]);
            }
        }, r => r.TID, r => r.Category, files);

        t.ReadCategorised<CustomHouseMisc>(Path("misc.txt"), "Misc", (m, g) =>
        {
            for (int i = 0; i < CustomHouseMisc.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((m.Graphics[i], $"piece {i + 1}"));
            }
        }, m => m.TID, m => m.Category, files);

        t.ReadCategorised<CustomHouseTeleport>(Path("teleprts.txt"), "Teleporters", (p, g) =>
        {
            for (int i = 0; i < CustomHouseTeleport.GRAPHICS_COUNT; i++)
            {
                g.Items.Add((p.Graphics[i], $"teleporter {i + 1}"));
                t.FloorIds.Add(p.Graphics[i]);
            }
        }, null, p => p.Category, files);

        string supp = Path("suppinfo.txt");
        if (File.Exists(supp))
        {
            foreach (string line in File.ReadLines(supp))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var info = new CustomHousePlaceInfo();
                if (info.Parse(line))
                {
                    t.PlaceInfo[info.Graphics[0]] = info;
                }
            }
        }
        else
        {
            t.Missing.Add("suppinfo.txt");
        }

        // A piece may sit in two groups (a wall piece that is also a window); keep each group's items distinct.
        foreach (PaletteGroup g in t.Groups)
        {
            g.Items = g.Items.Where(i => i.Id != 0).GroupBy(i => i.Id).Select(x => x.First()).ToList();
        }

        t.Groups.RemoveAll(g => g.Items.Count == 0);
        return t;
    }

    private void ReadCategorised<T>(string path, string kind, Action<T, PaletteGroup> items, Func<T, int> tid,
        Func<T, int> category, UOFileManager files) where T : CustomHouseObject, new()
    {
        if (!File.Exists(path))
        {
            Missing.Add(System.IO.Path.GetFileName(path));
            return;
        }

        int n = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var obj = new T();
            if (!obj.Parse(line))
            {
                continue;
            }

            n++;
            string style = null;
            if (tid != null && tid(obj) != 0)
            {
                try
                {
                    style = files.Clilocs.GetString(tid(obj));
                }
                catch (Exception)
                {
                }
            }

            var g = new PaletteGroup { Kind = kind, Name = string.IsNullOrWhiteSpace(style) ? $"{kind} {category(obj)}.{n}" : style };
            items(obj, g);
            Groups.Add(g);
        }
    }

    /// <summary>The table piece role of an id, for the pieces' tooltip ("Walls: stone, corner"), or null.</summary>
    public string RoleOf(ushort id)
    {
        foreach (PaletteGroup g in Groups)
        {
            foreach (var (gid, role) in g.Items)
            {
                if (gid == id)
                {
                    return $"{g.Kind}: {g.Name}, {role}";
                }
            }
        }

        return null;
    }
}
#endif
