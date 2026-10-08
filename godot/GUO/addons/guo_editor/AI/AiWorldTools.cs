#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GUO.Game.GameObjects;

/// <summary>World reads share the Nearby panel and modes' data; writes share WorldEditor and its undo.</summary>
internal static class AiWorldTools
{
    internal static void Register(AiToolHost host, SearchContext ctx)
    {
        host.Register(new AiToolHost.Tool
        {
            Name = "world_state",
            Description = "Read the World tab: active facet, pointer cell (or camera centre) x/y/z, picked object, open project name and View mode. Does not move the camera.",
            Run = _ => State(ctx),
        });
        host.Register(new AiToolHost.Tool
        {
            Name = "describe_cell",
            Description = "Read a cell on the active facet: land id/name/z and the full static stack, highest first, with id/name/z/height/hue/tiledata flags. Uses the Nearby tiles data. For another facet, use jump_world first.",
            Parameters = CellParams(),
            Run = a => Describe(ctx, a),
        });
        host.Register(new AiToolHost.Tool
        {
            Name = "walkable",
            Description = "Read the Walkability View verdict for a cell on the active facet: walkable, blocked, surface (floor/bridge/steps) or wet, plus standing heights. Uses the client's movement rules through the same mode data.",
            Parameters = CellParams(),
            Run = a => Walkable(ctx, a),
        });
        JsonObject stamp = CellParams();
        ((JsonObject)stamp["properties"])["graphic"] = new JsonObject { ["type"] = "integer", ["description"] = "static graphic id (decimal, 0..65535; must be in loaded tiledata)" };
        ((JsonObject)stamp["properties"])["hue"] = new JsonObject { ["type"] = "integer", ["description"] = "UO hue value, 0..65535; 0 is unhued" };
        stamp["required"] = new JsonArray("facet", "x", "y", "graphic", "hue");
        host.Register(new AiToolHost.Tool
        {
            Name = "stamp_static",
            Description = "WRITE: after the user approves this one call, stamp one static at the cell's land z in the currently open world project. Uses WorldEditor undo. Never writes the client installation; requires an open project and the active facet.",
            Parameters = stamp,
            ReadOnly = false,
            Run = a => Stamp(ctx, a),
        });
    }

    private static JsonObject CellParams()
    {
        JsonObject p = AiToolHost.Params(("facet", "integer", "active map number"),
            ("x", "integer", "map cell x"), ("y", "integer", "map cell y"));
        p["required"] = new JsonArray("facet", "x", "y");
        return p;
    }

    private static WorldView Ready(SearchContext ctx)
    {
        if (ctx?.World?.IsBooted != true || ctx.Data?.Files == null || ctx.World.Modes?.Data == null)
            throw new InvalidOperationException("the World tab is not ready; open it with jump_world first");
        return ctx.World;
    }

    private static int Integer(JsonNode args, string key)
    {
        if (args?[key] is JsonValue value && value.TryGetValue<int>(out int n)) return n;
        throw new ArgumentException($"{key} is a required integer");
    }

    private static (WorldView World, int Facet, int X, int Y) Cell(SearchContext ctx, JsonNode args)
    {
        WorldView view = Ready(ctx);
        int facet = Integer(args, "facet"), x = Integer(args, "x"), y = Integer(args, "y");
        if (facet != view.Host.Facet)
            throw new ArgumentException("facet must be the active World facet; use jump_world first");
        var sizes = ctx.Data.Files.Maps.MapsDefaultSize;
        if (facet < 0 || facet >= sizes.GetLength(0) || x < 0 || y < 0 ||
            x >= sizes[facet, 0] || y >= sizes[facet, 1] || !view.Modes.Data.InMap(x, y))
            throw new ArgumentException("cell is outside the active map");
        return (view, facet, x, y);
    }

    private static string State(SearchContext ctx)
    {
        WorldView view = Ready(ctx);
        WorldHost host = view.Host;
        GameObject picked = host.Picked as GameObject;
        var cell = view.Modes.Hover ?? (host.X, host.Y);
        JsonObject pick = picked == null ? null : new JsonObject
        {
            ["kind"] = picked.GetType().Name.ToLowerInvariant(),
            ["graphic"] = (int)picked.Graphic,
            ["name"] = picked is Entity entity ? entity.Name : ctx.Data.NameOf((uint)(picked.Graphic + (picked is Land ? 0 : EditorData.LandCount))),
            ["hue"] = (int)picked.Hue,
            ["x"] = (int)picked.X, ["y"] = (int)picked.Y, ["z"] = (int)picked.Z,
        };
        return new JsonObject
        {
            ["facet"] = host.Facet, ["x"] = cell.Item1, ["y"] = cell.Item2,
            ["z"] = view.Modes.Hover != null && picked != null ? picked.Z : host.Z,
            ["position_source"] = view.Modes.Hover != null ? "pointer" : "centre",
            ["picked"] = pick, ["project"] = host.Project?.Name,
            ["view_mode"] = string.IsNullOrEmpty(view.Modes.ModeName) ? "Off" : view.Modes.ModeName,
        }.ToJsonString();
    }

    private static string Describe(SearchContext ctx, JsonNode args)
    {
        var (view, facet, x, y) = Cell(ctx, args);
        WorldData data = view.Modes.Data;
        ushort land = data.LandId(x, y);
        var stack = new JsonArray();
        foreach (WorldStatic s in view.Editor.StaticsAt(facet, x, y).OrderByDescending(s => s.Z))
        {
            var tile = ctx.Data.Files.TileData.StaticData[s.Id];
            stack.Add(new JsonObject
            {
                ["id"] = (int)s.Id, ["name"] = ctx.Data.NameOf((uint)(EditorData.LandCount + s.Id)),
                ["z"] = (int)s.Z, ["height"] = (int)tile.Height, ["hue"] = (int)s.Hue,
                ["flags"] = tile.Flags.ToString(), ["flags_value"] = (ulong)tile.Flags,
            });
        }
        return new JsonObject
        {
            ["facet"] = facet, ["x"] = x, ["y"] = y,
            ["land"] = new JsonObject { ["id"] = (int)land, ["name"] = ctx.Data.NameOf(land), ["z"] = (int)data.LandZ(x, y) },
            ["statics"] = stack,
        }.ToJsonString();
    }

    private static string Walkable(SearchContext ctx, JsonNode args)
    {
        var (view, facet, x, y) = Cell(ctx, args);
        var heights = new JsonArray();
        foreach (sbyte z in view.Modes.Data.Standable(x, y)) heights.Add((int)z);
        return new JsonObject
        {
            ["facet"] = facet, ["x"] = x, ["y"] = y,
            ["verdict"] = view.Modes.Data.WalkAt(x, y).ToString().ToLowerInvariant(),
            ["standing_z"] = heights,
        }.ToJsonString();
    }

    private static string Stamp(SearchContext ctx, JsonNode args)
    {
        var (view, facet, x, y) = Cell(ctx, args);
        int graphic = Integer(args, "graphic"), hue = Integer(args, "hue");
        if (graphic < 0 || graphic > ushort.MaxValue || graphic >= ctx.Data.Files.TileData.StaticData.Length)
            throw new ArgumentException("graphic must be a loaded static tiledata id in 0..65535");
        if (hue < 0 || hue > ushort.MaxValue) throw new ArgumentException("hue must be 0..65535");
        if (view.Host.Project == null) throw new InvalidOperationException("open a world project before stamping");
        string relative = Path.GetRelativePath(Path.GetFullPath(ctx.Data.ClientData), view.Host.Project.Root);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("refusing to stamp a project inside the client installation");
        sbyte z = view.Modes.Data.LandZ(x, y);
        bool changed = view.Editor.Stamp(facet, x, y, z, (ushort)graphic, (ushort)hue);
        return new JsonObject
        {
            ["applied"] = changed, ["facet"] = facet, ["x"] = x, ["y"] = y,
            ["z"] = (int)z, ["graphic"] = graphic, ["hue"] = hue, ["project"] = view.Host.Project.Name,
        }.ToJsonString();
    }
}
#endif
