#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

/// <summary>
/// A scene pack (ADR-0027): the view as it is now, written to
/// <c>build/scene_packs/&lt;stamp&gt;/</c> for a reviewer, human or a vision
/// model run with the user's own tool. <c>shot.png</c> (the world frame, when
/// there is a display), one image per chosen render mode, and
/// <c>scene.json</c> (docs/data_formats.md section 22): the camera, the map
/// from pixels to cells, the legends and the objects with their screen boxes.
/// The editor sends it nowhere.
/// </summary>
internal static class ScenePack
{
    public const int Format = 1;
    public const int MaxObjects = 4000;

    /// <summary>The modes a pack carries when none were chosen.</summary>
    public static readonly string[] DefaultModes = { "Height", "Walkability", "Types" };

    private static string Hex(Color c) => $"#{(int)(c.R * 255):x2}{(int)(c.G * 255):x2}{(int)(c.B * 255):x2}";

    /// <summary>Writes the pack and returns its folder, or null when the world is not up.</summary>
    public static string Write(WorldView view, EditorData data, IEnumerable<string> modes, string root = null)
    {
        WorldModes node = view.Modes;
        ModeContext ctx = node.Context(false);
        if (ctx == null)
        {
            return null;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string dir = Path.Combine(root ?? Path.Combine(EditorData.RepoRoot, "build", "scene_packs"), stamp);
        Directory.CreateDirectory(dir);

        CellGeometry g = ctx.Geo;
        Image shot = view.Capture();
        bool haveShot = shot != null && !shot.IsEmpty() && shot.GetWidth() == g.Width && shot.GetHeight() == g.Height;
        if (haveShot)
        {
            shot.SavePng(Path.Combine(dir, "shot.png"));
        }

        // One image per mode, drawn by the same code as the World tab; over the shot when there is one.
        ctx.Tinted = haveShot;
        var modeList = new JsonArray();
        foreach (string name in modes.Distinct())
        {
            IWorldMode mode = node.ModeNamed(name);
            if (mode == null)
            {
                continue;
            }

            var paint = new ImagePaint(g.Width, g.Height, haveShot ? shot : null);
            mode.Draw(paint, ctx);
            string file = mode.Name.ToLowerInvariant().Replace(' ', '_') + ".png";
            paint.ToImage().SavePng(Path.Combine(dir, file));
            var legend = new JsonArray();
            foreach (LegendItem l in mode.Legend(ctx))
            {
                legend.Add(new JsonObject { ["label"] = l.Label, ["colour"] = Hex(l.Colour) });
            }

            modeList.Add(new JsonObject { ["name"] = mode.Name, ["file"] = file, ["summary"] = mode.Summary, ["legend"] = legend });
        }

        // Pixel to cell: pixel(x, y, z) = origin + (x - cx) * x_step + (y - cy) * y_step + (z - cz) * z_step.
        int cx = g.CentreX, cy = g.CentreY, cz = g.CentreZ;
        Vector2 o = g.Project(cx, cy, cz);
        Vector2 xs = g.Project(cx + 1, cy, cz) - o, ys = g.Project(cx, cy + 1, cz) - o, zs = g.Project(cx, cy, cz + 1) - o;
        var grid = new JsonArray();
        for (int py = 0; py < g.Height; py += 80)
        {
            for (int px = 0; px < g.Width; px += 80)
            {
                (float X, float Y) c = g.CellAt(new Vector2(px, py), cz);
                grid.Add(new JsonObject { ["px"] = px, ["py"] = py, ["x"] = Math.Round(c.X, 2), ["y"] = Math.Round(c.Y, 2) });
            }
        }

        float scale = xs.X / 22f;   // viewport pixels per world pixel
        var objects = new JsonArray();
        var art = new Dictionary<ushort, (int W, int H)>();
        foreach (CellQuad q in g.Cells())
        {
            foreach (ObjInfo ob in ctx.Data.Objects(q.X, q.Y))
            {
                if (objects.Count >= MaxObjects)
                {
                    break;
                }

                if (!art.TryGetValue(ob.Graphic, out var wh))
                {
                    Image im = data?.ArtImage(EditorData.LandCount + ob.Graphic);
                    art[ob.Graphic] = wh = im == null ? (44, 44) : (im.GetWidth(), im.GetHeight());
                }

                Vector2 top = g.Project(q.X, q.Y, ob.Z);
                float bx = top.X + 22 * scale, by = top.Y + 44 * scale, w = wh.W * scale, h = wh.H * scale;
                if (bx + w < 0 || bx - w > g.Width || by < 0 || by - h > g.Height)
                {
                    continue;
                }

                objects.Add(new JsonObject
                {
                    ["kind"] = ob.IsMulti ? "multi" : ob.IsItem ? "item" : "static",
                    ["graphic"] = $"0x{ob.Graphic:X4}",
                    ["name"] = ob.Name,
                    ["type"] = ob.Kind.ToString().ToLowerInvariant(),
                    ["x"] = q.X, ["y"] = q.Y, ["z"] = ob.Z, ["height"] = ob.Height,
                    ["box"] = new JsonArray((int)(bx - w / 2), (int)(by - h), (int)(bx + w / 2), (int)by),
                });
            }
        }

        var layers = new JsonObject();
        foreach (IMapLayer l in node.Layers.All.Where(l => l.On))
        {
            var items = new JsonArray();
            foreach (LayerItem it in l.Items(g.Facet))
            {
                Vector2 at = g.Project(it.X + 0.5f, it.Y + 0.5f, it.Z);
                items.Add(new JsonObject
                {
                    ["label"] = it.Label, ["x"] = it.X, ["y"] = it.Y, ["z"] = it.Z, ["detail"] = it.Detail,
                    ["px"] = (int)at.X, ["py"] = (int)at.Y, ["on_view"] = at.X >= 0 && at.Y >= 0 && at.X < g.Width && at.Y < g.Height,
                    ["sextant"] = Coordinates.Sextant(g.Facet, it.X, it.Y),
                });
            }

            layers[l.Name] = items;
        }

        var scene = new JsonObject
        {
            ["format"] = Format,
            ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["facet"] = g.Facet,
            ["camera"] = new JsonObject
            {
                ["centre"] = new JsonArray(cx, cy, cz), ["zoom"] = g.Zoom,
                ["viewport"] = new JsonArray(g.Width, g.Height),
                ["sextant"] = Coordinates.Sextant(g.Facet, cx, cy),
            },
            ["shot"] = haveShot ? "shot.png" : null,
            ["pixel_to_cell"] = new JsonObject
            {
                ["note"] = "pixel(x,y,z) = origin + (x-cx)*x_step + (y-cy)*y_step + (z-cz)*z_step; grid[] gives the cell under a pixel on flat ground at the centre's z",
                ["origin"] = new JsonArray(o.X, o.Y), ["x_step"] = new JsonArray(xs.X, xs.Y),
                ["y_step"] = new JsonArray(ys.X, ys.Y), ["z_step"] = new JsonArray(zs.X, zs.Y),
                ["grid"] = grid,
            },
            ["modes"] = modeList,
            ["objects"] = objects,
            ["layers"] = layers,
        };
        File.WriteAllText(Path.Combine(dir, "scene.json"), scene.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return dir;
    }
}
#endif
