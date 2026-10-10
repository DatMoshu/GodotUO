#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>A mode that colours each cell: subclasses say which colour (or none).</summary>
internal abstract class CellMode : IWorldMode
{
    public abstract string Name { get; }
    public abstract string Summary { get; }
    public abstract string Meaning { get; }
    public abstract IReadOnlyList<LegendItem> Legend(ModeContext ctx);
    public abstract string Classify(ModeContext ctx, int x, int y);

    /// <summary>The cell's fill, or null to leave the world showing.</summary>
    protected abstract Color? Fill(ModeContext ctx, int x, int y);

    public virtual string Describe(ModeContext ctx, int x, int y) => $"{x},{y}  {Name}: {Classify(ctx, x, y)}";

    public virtual void Draw(IPaint p, ModeContext ctx)
    {
        foreach (CellQuad q in ctx.Geo.Cells())
        {
            if (Fill(ctx, q.X, q.Y) is { } c)
            {
                p.Quad(q.Top, q.Right, q.Bottom, q.Left, ctx.Tint(c));
            }
        }
    }
}

/// <summary>The registry of modes, in the View menu's order.</summary>
internal static class WorldModeList
{
    public static IWorldMode[] All() => new IWorldMode[]
    {
        new HeightMode(), new WalkMode(), new ReachMode(), new TypesMode(), new IdsMode(),
        new LandMeshMode(), new ProblemsMode(), new DiffMode(),
    };
}

internal sealed class HeightMode : CellMode
{
    private static readonly Color[] Ramp =
    {
        new(0.10f, 0.20f, 0.70f), new(0.10f, 0.65f, 0.75f), new(0.20f, 0.75f, 0.25f), new(0.95f, 0.90f, 0.20f),
        new(0.95f, 0.45f, 0.10f), new(0.90f, 0.10f, 0.10f), new(1f, 1f, 1f),
    };

    private int _min, _max = 1;

    public override string Name => "Height";
    public override string Meaning => "Colour shows how high the ground is: blue is low, red and white are high.";
    public override string Summary => "z heatmap of the land and walkable surfaces; contour lines every 5 z";

    /// <summary>The z range on view at the last draw (or the last <see cref="Measure"/>).</summary>
    public (int Min, int Max) Range => (_min, _max);

    public static Color Colour(float t)
    {
        t = Math.Clamp(t, 0f, 1f) * (Ramp.Length - 1);
        int i = Math.Min((int)t, Ramp.Length - 2);
        return Ramp[i].Lerp(Ramp[i + 1], t - i);
    }

    public void Measure(ModeContext ctx)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (CellQuad q in ctx.Geo.Cells())
        {
            int z = ctx.Data.SurfaceZ(q.X, q.Y);
            lo = Math.Min(lo, z);
            hi = Math.Max(hi, z);
        }

        _min = lo == int.MaxValue ? 0 : lo;
        _max = hi == int.MinValue ? 1 : Math.Max(hi, _min + 1);
    }

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) =>
        Enumerable.Range(0, 5).Select(i =>
        {
            float t = i / 4f;
            return new LegendItem($"z {_min + (int)Math.Round((_max - _min) * t)}", Colour(t));
        }).Append(new LegendItem("contour every 5 z", new Color(0, 0, 0))).ToList();

    protected override Color? Fill(ModeContext ctx, int x, int y) =>
        ctx.Data.InMap(x, y) ? Colour((ctx.Data.SurfaceZ(x, y) - _min) / (float)(_max - _min)) : null;

    public override string Classify(ModeContext ctx, int x, int y) => $"z {ctx.Data.SurfaceZ(x, y)}";

    public override void Draw(IPaint p, ModeContext ctx)
    {
        Measure(ctx);
        base.Draw(p, ctx);
        var line = new Color(0f, 0f, 0f, 0.8f);
        foreach (CellQuad q in ctx.Geo.Cells())
        {
            int x = q.X, y = q.Y;
            int zt = ctx.Data.LandZ(x, y), zr = ctx.Data.LandZ(x + 1, y), zl = ctx.Data.LandZ(x, y + 1);
            if (Level(zt) != Level(zr))
            {
                p.Line(q.Top, q.Right, line, 1.5f);
            }

            if (Level(zt) != Level(zl))
            {
                p.Line(q.Top, q.Left, line, 1.5f);
            }
        }
    }

    private static int Level(int z) => (int)Math.Floor(z / 5.0);
}

internal sealed class WalkMode : CellMode
{
    public static readonly Color Walkable = new(0.20f, 0.80f, 0.30f), Surface = new(0.15f, 0.75f, 0.80f),
        Wet = new(0.20f, 0.35f, 0.90f), Blocked = new(0.85f, 0.20f, 0.20f), Pending = new(0.5f, 0.5f, 0.5f);

    public override string Name => "Walkability";
    public override string Meaning => "Colour shows where a player can stand and where they are stopped.";
    public override string Summary => "what the client's own movement rules (Pathfinder.CanWalk) let a player stand on";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem("walkable ground", Walkable), new LegendItem("walkable on an item (floor, bridge)", Surface),
        new LegendItem("water", Wet), new LegendItem("blocked: a player cannot stand here", Blocked),
    };

    public static Color Of(Walk w) => w switch
    {
        Walk.Walkable => Walkable, Walk.Surface => Surface, Walk.Wet => Wet, Walk.Blocked => Blocked, _ => Pending,
    };

    protected override Color? Fill(ModeContext ctx, int x, int y)
    {
        if (!ctx.Data.InMap(x, y))
        {
            return null;
        }

        Walk w = ctx.OverBudget ? ctx.Data.PeekWalk(x, y) : ctx.Data.WalkAt(x, y);
        return Of(w);
    }

    public override string Classify(ModeContext ctx, int x, int y) => ctx.Data.WalkAt(x, y).ToString().ToLowerInvariant();

    public override string Describe(ModeContext ctx, int x, int y) =>
        $"{x},{y}  {Classify(ctx, x, y)}  standing z: [{string.Join(", ", ctx.Data.Standable(x, y))}]";
}

internal sealed class ReachMode : CellMode
{
    public static readonly Color Reached = new(0.20f, 0.80f, 0.30f), Unreachable = new(1f, 0.55f, 0.05f);

    public override string Name => "Reachability";
    public override string Meaning => "Click a cell: colour shows where a player can walk to from it.";
    public override string Summary => "click a cell: green is reachable from it by the client's walking rules, orange is walkable but cut off";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem(ctx.Origin == null ? "click a cell to fill from" : ctx.Reach is { Valid: false } ? "origin is not walkable" : "a player can walk here from it", Reached),
        new LegendItem("walkable, but cut off from it", Unreachable),
    };

    protected override Color? Fill(ModeContext ctx, int x, int y)
    {
        ReachFill r = ctx.Reach;
        if (r == null || Math.Abs(x - r.OriginX) > r.Radius || Math.Abs(y - r.OriginY) > r.Radius)
        {
            return null;
        }

        if (r.Reached(x, y))
        {
            return Reached;
        }

        Walk w = ctx.OverBudget ? ctx.Data.PeekWalk(x, y) : ctx.Data.WalkAt(x, y);
        return w is Walk.Walkable or Walk.Surface ? Unreachable : null;
    }

    public override string Classify(ModeContext ctx, int x, int y)
    {
        if (ctx.Reach == null)
        {
            return "no origin";
        }

        if (ctx.Reach.Reached(x, y))
        {
            return "reachable";
        }

        return ctx.Data.WalkAt(x, y) is Walk.Walkable or Walk.Surface ? "unreachable" : "blocked";
    }

    public override void Draw(IPaint p, ModeContext ctx)
    {
        base.Draw(p, ctx);
        if (ctx.Reach != null)
        {
            foreach (CellQuad q in ctx.Geo.Cells().Where(q => q.X == ctx.Reach.OriginX && q.Y == ctx.Reach.OriginY))
            {
                p.Line(q.Top, q.Right, Colors.White, 2);
                p.Line(q.Right, q.Bottom, Colors.White, 2);
                p.Line(q.Bottom, q.Left, Colors.White, 2);
                p.Line(q.Left, q.Top, Colors.White, 2);
            }
        }
    }
}

internal sealed class TypesMode : CellMode
{
    public static Color Of(Kind k) => k switch
    {
        Kind.Land => new Color(0.30f, 0.55f, 0.25f),
        Kind.Floor => new Color(0.85f, 0.78f, 0.55f),
        Kind.Wall => new Color(0.55f, 0.55f, 0.60f),
        Kind.Window => new Color(0.45f, 0.80f, 0.95f),
        Kind.Door => new Color(0.95f, 0.55f, 0.10f),
        Kind.Roof => new Color(0.70f, 0.20f, 0.20f),
        Kind.Stairs => new Color(0.85f, 0.30f, 0.85f),
        Kind.Foliage => new Color(0.10f, 0.40f, 0.12f),
        Kind.Water => new Color(0.15f, 0.35f, 0.85f),
        _ => new Color(0.95f, 0.90f, 0.30f),
    };

    public override string Name => "Types";
    public override string Meaning => "Colour shows what each cell holds: wall, floor, roof, water and so on.";
    public override string Summary => "what each cell is: land, floor, wall, window, door, roof, stairs, foliage, water, prop (tiledata flags and names)";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) =>
        Enum.GetValues<Kind>().Select(k => new LegendItem(k.ToString().ToLowerInvariant(), Of(k))).ToList();

    protected override Color? Fill(ModeContext ctx, int x, int y) => ctx.Data.InMap(x, y) ? Of(ctx.Data.KindAt(x, y)) : null;

    public override string Classify(ModeContext ctx, int x, int y) => ctx.Data.KindAt(x, y).ToString().ToLowerInvariant();

    public override string Describe(ModeContext ctx, int x, int y) => ctx.Data.Describe(x, y) + $"\ntype: {Classify(ctx, x, y)}";
}

internal sealed class IdsMode : CellMode
{
    public override string Name => "IDs";
    public override string Meaning => "Each kind of item gets its own colour, so the map looks like a patchwork. That is normal, not damage.";
    public override string Summary => "a colour per graphic (the topmost object on the cell); hover shows the graphic and name";

    public static Color Hash(int graphic)
    {
        float h = (graphic * 0.61803398f) % 1f;
        return Color.FromHsv(h, 0.7f, 0.9f);
    }

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem("one colour per kind of item (the top one)", Hash(0x0E75)), new LegendItem("no item: the ground, darker", Hash(3) * 0.4f),
    };

    private static ObjInfo? Top(WorldData d, int x, int y)
    {
        ObjInfo? best = null;
        foreach (ObjInfo o in d.Objects(x, y))
        {
            if (!o.IsItem && (best == null || o.Top >= best.Value.Top))
            {
                best = o;
            }
        }

        return best;
    }

    protected override Color? Fill(ModeContext ctx, int x, int y)
    {
        if (!ctx.Data.InMap(x, y))
        {
            return null;
        }

        return Top(ctx.Data, x, y) is { } o ? Hash(o.Graphic) : Hash(ctx.Data.LandId(x, y)) * 0.4f;
    }

    public override string Classify(ModeContext ctx, int x, int y) =>
        Top(ctx.Data, x, y) is { } o ? $"0x{o.Graphic:X4}" : $"land 0x{ctx.Data.LandId(x, y):X4}";

    public override string Describe(ModeContext ctx, int x, int y) => ctx.Data.Describe(x, y);
}

internal sealed class LandMeshMode : CellMode
{
    public static readonly Color Stretched = new(1f, 0.85f, 0.1f);

    public override string Name => "Land mesh";
    public override string Meaning => "Lines show the ground's shape; yellow tiles are stretched over a slope.";
    public override string Summary => "the land's wireframe at its corner heights; tiles the client stretches are yellow";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem("ground stretched over a slope", Stretched), new LegendItem("flat ground", new Color(0.8f, 0.8f, 0.85f)),
    };

    protected override Color? Fill(ModeContext ctx, int x, int y) =>
        !ctx.Data.InMap(x, y) ? null : ctx.Data.Stretched(x, y) ? Stretched : (ctx.Tinted ? null : new Color(0.12f, 0.12f, 0.15f));

    public override string Classify(ModeContext ctx, int x, int y) => ctx.Data.Stretched(x, y) ? "stretched" : "flat";

    public override void Draw(IPaint p, ModeContext ctx)
    {
        base.Draw(p, ctx);
        var wire = new Color(0.85f, 0.9f, 1f, 0.8f);
        foreach (CellQuad q in ctx.Geo.Cells())
        {
            if (!ctx.Data.InMap(q.X, q.Y))
            {
                continue;
            }

            p.Line(q.Top, q.Right, wire);
            p.Line(q.Top, q.Left, wire);
        }
    }
}

internal sealed class ProblemsMode : CellMode
{
    public static readonly Color Hole = new(1f, 0.1f, 0.9f), ZFight = new(1f, 0.15f, 0.1f), OnWater = new(0.1f, 0.95f, 0.95f);

    public override string Name => "Problems";
    public override string Meaning => "Only mistakes are coloured: holes in floors, items stacked in one spot, items on water.";
    public override string Summary => "holes in floors, statics at the same cell and z (z-fights), statics standing on water";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem("hole in a floor", Hole), new LegendItem("two items in one spot at one height (they flicker)", ZFight), new LegendItem("item standing on water", OnWater),
    };

    protected override Color? Fill(ModeContext ctx, int x, int y)
    {
        Problem p = ctx.Data.ProblemsAt(x, y);
        return (p & Problem.ZFight) != 0 ? ZFight : (p & Problem.Hole) != 0 ? Hole : (p & Problem.OnWater) != 0 ? OnWater : null;
    }

    public override string Classify(ModeContext ctx, int x, int y)
    {
        Problem p = ctx.Data.ProblemsAt(x, y);
        return p == Problem.None ? "ok" : p.ToString().ToLowerInvariant().Replace(", ", "+");
    }
}

internal sealed class DiffMode : CellMode
{
    public static readonly Color Changed = new(1f, 0.6f, 0.1f);

    public override string Name => "Project diff";
    public override string Meaning => "Colour marks the parts of the map this project changed; the rest is your own install.";
    public override string Summary => "the 8x8 blocks the world project replaced; everything else is the install's";

    public override IReadOnlyList<LegendItem> Legend(ModeContext ctx) => new[]
    {
        new LegendItem($"changed by the project ({ctx.ChangedBlocks.Count} blocks here)", Changed),
    };

    protected override Color? Fill(ModeContext ctx, int x, int y) =>
        ctx.ChangedBlocks.Contains(((long)(x >> 3) << 20) | (uint)(y >> 3)) ? Changed : null;

    public override string Classify(ModeContext ctx, int x, int y) =>
        ctx.ChangedBlocks.Contains(((long)(x >> 3) << 20) | (uint)(y >> 3)) ? "changed" : "install";
}
#endif
