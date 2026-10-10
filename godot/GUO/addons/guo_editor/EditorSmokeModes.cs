#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Assets;

/// <summary>
/// The smoke stage for ADR-0027: every render mode and map layer at the known
/// spot (map0 1496,1628). Each mode is chosen through the View menu's own
/// call, drawn onto a CPU image through the same code the World tab uses
/// (so it also runs headless), saved under <c>modes/</c> in the output
/// folder, and checked on a cell with a known answer.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _modesReport = new();
    private const int ModeX = 1496, ModeY = 1628;

    private void ModeFail(string why)
    {
        _modesReport["ok"] = false;
        _failures.Add($"Modes: {why}");
    }

    private void ModeExpect(bool ok, string what)
    {
        _modesReport[what] = ok;
        if (!ok)
        {
            ModeFail(what);
        }
    }

    internal static string FixtureShardFolder() => FixtureShard();

    private static string FixtureShard() => Path.Combine(EditorData.RepoRoot, "tools", "editor_smoke", "fixtures", "shard");

    private ImagePaint RenderMode(IWorldMode mode, ModeContext ctx, string file)
    {
        var paint = new ImagePaint(ctx.Geo.Width, ctx.Geo.Height);
        mode.Draw(paint, ctx);
        Directory.CreateDirectory(Path.Combine(_out, "modes"));
        paint.ToImage().SavePng(Path.Combine(_out, "modes", file));
        return paint;
    }

    private IEnumerable<(int X, int Y)> NearCentre(int r)
    {
        for (int x = ModeX - r; x <= ModeX + r; x++)
        {
            for (int y = ModeY - r; y <= ModeY + r; y++)
            {
                yield return (x, y);
            }
        }
    }

    private bool Painted(ImagePaint p, Color c, int tolerance = 6)
    {
        for (int y = 0; y < p.Height; y += 2)
        {
            for (int x = 0; x < p.Width; x += 2)
            {
                Color q = p.PixelAt(x, y);
                if (Math.Abs(q.R - c.R) * 255 < tolerance && Math.Abs(q.G - c.G) * 255 < tolerance && Math.Abs(q.B - c.B) * 255 < tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void AddModeSteps()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        _worldReport["modes"] = _modesReport;
        ModeContext ctx = null;
        WorldData data = null;

        _steps.Add((1, () =>
        {
            _world.SetFixedSize(new Vector2I(1280, 720));
            _world.GoTo(0, ModeX, ModeY);
            _world.Modes.Invalidate();
        }));

        _steps.Add((6, () =>
        {
            _world.SetSolid(true);   // solid fills, so the colour checks read the legend's colours exactly
            ctx = _world.Modes.Context(false);
            data = _world.Modes.Data;
            ModeExpect(ctx != null, "context");
            if (ctx == null)
            {
                return;
            }

            _modesReport["view_cells"] = ctx.Geo.Cells().Count();
            _modesReport["toolbar_min_width"] = _world.ToolbarMinWidth();
            ModeExpect(_world.ToolbarMinWidth() <= 1920, "toolbar_fits_1920");

            // Height
            Expect2(_world.SetViewMode("Height"), "view_height");
            var height = (HeightMode)_world.Modes.Mode;
            ImagePaint img = RenderMode(height, ctx, "height.png");
            _modesReport["height_range"] = new[] { height.Range.Min, height.Range.Max };
            ModeExpect(height.Range.Min < height.Range.Max && height.Legend(ctx).Count >= 5, "height_legend_range");
            ModeExpect(height.Classify(ctx, ModeX, ModeY).StartsWith("z "), "height_classify");
            ModeExpect(Colours(img.ToImage()) >= 4, "height_painted");

            // Walkability: every impassable wall with nothing standable-on beside it is blocked, the centre road is open.
            Expect2(_world.SetViewMode("Walkability"), "view_walk");
            var walk = (WalkMode)_world.Modes.Mode;
            int walls = 0, wallsBlocked = 0;
            var wallCells = new List<string>();
            foreach (var (x, y) in NearCentre(24))
            {
                var objs = data.Objects(x, y);
                if (objs.Any(o => !o.IsItem && o.Kind == Kind.Wall && (o.Flags & TileFlag.Impassable) != 0 && o.Height >= 20)
                    && !objs.Any(o => o.Surface || o.Bridge))
                {
                    walls++;
                    if (data.WalkAt(x, y) == Walk.Blocked)
                    {
                        wallsBlocked++;
                    }
                    else if (wallCells.Count < 4)
                    {
                        wallCells.Add($"{x},{y}:{data.WalkAt(x, y)}");
                    }
                }
            }

            _modesReport["walk_walls"] = new[] { walls, wallsBlocked };
            _modesReport["walk_wall_misses"] = wallCells;
            ModeExpect(walls > 0 && wallsBlocked == walls, "walk_wall_cells_blocked");
            _modesReport["walk_centre"] = walk.Classify(ctx, ModeX, ModeY);
            ModeExpect(data.WalkAt(ModeX, ModeY) is Walk.Walkable or Walk.Surface, "walk_centre_open");
            var counts = NearCentre(24).GroupBy(c => data.WalkAt(c.X, c.Y)).ToDictionary(g => g.Key.ToString(), g => g.Count());
            _modesReport["walk_counts"] = counts;
            ModeExpect(counts.Count >= 2, "walk_classes_vary");
            RenderMode(walk, ctx, "walkability.png");
            ModeExpect(walk.Legend(ctx).Count == 4, "walk_legend");

            // Reachability
            Expect2(_world.SetViewMode("Reachability"), "view_reach");
            _world.Modes.SetOrigin(ModeX, ModeY, data.LandZ(ModeX, ModeY));
            ReachFill reach = _world.Modes.Reach;
            reach.Finish();
            ctx = _world.Modes.Context(false);
            _modesReport["reach_count"] = reach.Count;
            ModeExpect(reach.Valid && reach.Count > 100, "reach_fills");
            ModeExpect(reach.Reached(ModeX, ModeY), "reach_origin");
            int unreachable = NearCentre(24).Count(c => _world.Modes.Mode.Classify(ctx, c.X, c.Y) == "unreachable");
            _modesReport["reach_unreachable_cells"] = unreachable;
            ModeExpect(unreachable > 0, "reach_finds_cut_off_cells");
            ImagePaint rp = RenderMode(_world.Modes.Mode, ctx, "reachability.png");
            ModeExpect(Painted(rp, ReachMode.Reached) && Painted(rp, ReachMode.Unreachable), "reach_green_and_orange");

            // Types
            Expect2(_world.SetViewMode("Types"), "view_types");
            var kinds = NearCentre(24).Select(c => data.KindAt(c.X, c.Y)).Distinct().ToList();
            _modesReport["type_kinds"] = kinds.Select(k => k.ToString()).ToList();
            ModeExpect(kinds.Contains(Kind.Wall) && kinds.Contains(Kind.Land) && kinds.Count >= 5, "types_vary");
            RenderMode(_world.Modes.Mode, ctx, "types.png");

            // IDs
            Expect2(_world.SetViewMode("IDs"), "view_ids");
            var withStatic = NearCentre(24).First(c => data.Objects(c.X, c.Y).Any(o => !o.IsItem));
            ModeExpect(_world.Modes.Mode.Classify(ctx, withStatic.X, withStatic.Y).StartsWith("0x"), "ids_classify");
            ModeExpect(_world.Modes.Mode.Describe(ctx, withStatic.X, withStatic.Y).Contains("0x"), "ids_hover_names_graphic");
            RenderMode(_world.Modes.Mode, ctx, "ids.png");

            // Land mesh: stretched tiles somewhere in the hills of Yew.
            Expect2(_world.SetViewMode("Land mesh"), "view_mesh");
            int stretched = 0;
            for (int x = 600; x < 680; x++)
            {
                for (int y = 820; y < 900; y++)
                {
                    stretched += data.Stretched(x, y) ? 1 : 0;
                }
            }

            _modesReport["mesh_stretched_yew"] = stretched;
            ModeExpect(stretched > 0, "mesh_finds_stretched_land");
            RenderMode(_world.Modes.Mode, ctx, "land_mesh.png");

            // Problems
            Expect2(_world.SetViewMode("Problems"), "view_problems");
            var pc = NearCentre(40).GroupBy(c => _world.Modes.Mode.Classify(ctx, c.X, c.Y)).ToDictionary(g => g.Key, g => g.Count());
            _modesReport["problem_counts"] = pc;
            ModeExpect(pc.ContainsKey("ok"), "problems_classify");
            RenderMode(_world.Modes.Mode, ctx, "problems.png");

            // Project diff: the edit script's stamped block is the project's.
            Expect2(_world.SetViewMode("Project diff"), "view_diff");
            ModeExpect(_world.Modes.Mode.Classify(ctx, EditX, EditY) == "changed", "diff_marks_project_block");
            ModeExpect(_world.Modes.Mode.Classify(ctx, ModeX, ModeY) == "install", "diff_leaves_install_block");
            RenderMode(_world.Modes.Mode, ctx, "project_diff.png");

            // Solid fill toggles the alpha.
            _world.SetSolid(true);
            ModeExpect(_world.Modes.Context(false).Alpha > 0.99f, "solid_opaque");
            _world.SetSolid(false);
            ModeExpect(_world.Modes.Context(false).Alpha < 0.99f, "tinted_translucent");
            _world.SetViewMode("Walkability");
        }));

        _steps.Add((4, () =>
        {
            ModeExpect(_world.Chip.Visible && _world.Chip.Rows == 4, "legend_chip_shows_walkability");
            CheckLegendBelowHint();
            _world.SetViewMode("Off");
        }));

        AddLayerSteps();
    }

    private void Expect2(bool ok, string what) => ModeExpect(ok, what);
}
#endif
