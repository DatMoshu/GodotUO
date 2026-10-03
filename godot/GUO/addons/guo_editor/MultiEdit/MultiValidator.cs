#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using GUO.Assets;

public enum FindingSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One thing the validator found. <see cref="Uid"/> is -1 for a finding about a cell or the whole multi.</summary>
public sealed record Finding(string Kind, FindingSeverity Severity, int Uid, int X, int Y, int Z, string Message);

public sealed class ValidationResult
{
    public List<Finding> Findings { get; } = new();

    /// <summary>Standing places: x, y and the z a walker stands at.</summary>
    public List<(int X, int Y, int Z)> Walkable { get; } = new();

    public bool HasErrors => Findings.Any(f => f.Severity == FindingSeverity.Error);

    public int Count(string kind) => Findings.Count(f => f.Kind == kind);

    public FindingSeverity? WorstFor(int uid)
    {
        FindingSeverity? worst = null;
        foreach (Finding f in Findings)
        {
            if (f.Uid == uid && (worst == null || f.Severity > worst))
            {
                worst = f.Severity;
            }
        }

        return worst;
    }
}

/// <summary>
/// Live validation of a multi document (ADR-0031). A pure function of the parts and tiledata: the rules of
/// tools/multi/validate.py (ids with art, z range, component count, walls closed, a door) plus walkable
/// surfaces with headroom, double surfaces, components crossing into the next story, and ClassicUO's
/// legality grid. Errors block a save; everything else is a warning or a note.
/// </summary>
internal static class MultiValidator
{
    /// <summary>The shard reads an entry into 64 KB: (0x10000 - 8) / 14 components (tools/multi/multifile.py).</summary>
    public const int MaxComponents = (0x10000 - 8) / 14;

    public const int Headroom = 16;

    public static ValidationResult Run(IReadOnlyList<MultiPart> parts, StaticTiles[] tiles, Func<ushort, bool> hasArt, HouseTables tables)
    {
        var r = new ValidationResult();
        StaticTiles T(ushort id) => id < tiles.Length ? tiles[id] : default;
        int Calc(ushort id) => T(id).IsBridge ? T(id).Height / 2 : T(id).Height;

        // --- ids, z, size -------------------------------------------------------
        foreach (MultiPart p in parts)
        {
            if (p.Id >= tiles.Length)
            {
                r.Findings.Add(new Finding("unknown-id", FindingSeverity.Error, p.Uid, p.X, p.Y, p.Z, $"0x{p.Id:X4} is not in tiledata"));
            }
            else if (p.Shown && !hasArt(p.Id))
            {
                r.Findings.Add(new Finding("unknown-id", FindingSeverity.Error, p.Uid, p.X, p.Y, p.Z, $"0x{p.Id:X4} has no art"));
            }

            if (p.Z < -128 || p.Z > 127)
            {
                r.Findings.Add(new Finding("z-range", FindingSeverity.Error, p.Uid, p.X, p.Y, p.Z, $"z {p.Z} is outside -128..127"));
            }
        }

        if (parts.Count > MaxComponents)
        {
            r.Findings.Add(new Finding("too-many", FindingSeverity.Error, -1, 0, 0, 0, $"{parts.Count} components, the shard reads at most {MaxComponents}"));
        }

        var shown = parts.Where(p => p.Shown && p.Id < tiles.Length).ToList();
        var cells = shown.GroupBy(p => (p.X, p.Y)).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Z).ToList());

        // --- duplicates -----------------------------------------------------------
        foreach (var g in shown.GroupBy(p => (p.X, p.Y, p.Z, p.Id)).Where(g => g.Count() > 1))
        {
            foreach (MultiPart p in g.Skip(1))
            {
                r.Findings.Add(new Finding("duplicate", FindingSeverity.Warning, p.Uid, p.X, p.Y, p.Z, $"0x{p.Id:X4} is there twice"));
            }
        }

        // --- crossing into the next story ------------------------------------------
        foreach (MultiPart p in shown)
        {
            int h = T(p.Id).Height;
            if (h == 0)
            {
                continue;
            }

            int story = Stories.StoryOf(p.Z);
            int ceiling = story < 0 ? Stories.FloorZ : Stories.ZOf(story + 1);
            if (p.Z + h > ceiling)
            {
                r.Findings.Add(new Finding("cross-story", FindingSeverity.Warning, p.Uid, p.X, p.Y, p.Z,
                    $"0x{p.Id:X4} reaches z {p.Z + h}, into the floor at {ceiling}"));
            }
        }

        // --- walkable surfaces, double surfaces ----------------------------------------
        foreach (var (cell, list) in cells)
        {
            foreach (MultiPart p in list)
            {
                StaticTiles td = T(p.Id);
                if (!td.IsSurface || td.IsImpassable)
                {
                    continue;
                }

                int top = p.Z + Calc(p.Id);
                bool blocked = false;
                foreach (MultiPart q in list)
                {
                    if (q.Uid == p.Uid)
                    {
                        continue;
                    }

                    StaticTiles qt = T(q.Id);
                    bool qSurface = qt.IsSurface && !qt.IsImpassable;
                    if (q.Z >= top && q.Z < top + Headroom && (qSurface || qt.IsImpassable))
                    {
                        blocked = true;
                        if (qSurface)
                        {
                            r.Findings.Add(new Finding("double-surface", FindingSeverity.Warning, q.Uid, q.X, q.Y, q.Z,
                                $"0x{q.Id:X4} is a surface {q.Z - top} above 0x{p.Id:X4}: no headroom ({Headroom} needed)"));
                        }
                    }
                    else if (qt.IsImpassable && q.Z >= p.Z && q.Z < top)
                    {
                        blocked = true;
                    }
                }

                if (!blocked)
                {
                    r.Walkable.Add((p.X, p.Y, top));
                }
            }
        }

        // --- a door --------------------------------------------------------------------
        bool IsDoor(MultiPart p) => (T(p.Id).Flags & TileFlag.Door) != 0 || (tables?.DoorIds.Contains(p.Id) ?? false);
        if (parts.Count > 0 && !parts.Any(IsDoor))
        {
            r.Findings.Add(new Finding("no-door", FindingSeverity.Warning, -1, 0, 0, 0, "no door piece (the shard adds real doors at the hidden door markers)"));
        }

        // --- walls closed on the first storey ------------------------------------------------
        WallsClosed(r, shown, T, IsDoor);

        // --- the client's legality grid ---------------------------------------------------------
        HashSet<int> illegal = LegalityGrid.Illegal(parts, tiles, tables, out string note);
        foreach (int uid in illegal)
        {
            MultiPart p = parts.First(x => x.Uid == uid);
            r.Findings.Add(new Finding("illegal", FindingSeverity.Warning, uid, p.X, p.Y, p.Z, $"0x{p.Id:X4} is not legal in the client's house customiser"));
        }

        if (note != null)
        {
            r.Findings.Add(new Finding("legality-skipped", FindingSeverity.Info, -1, 0, 0, 0, note));
        }

        return r;
    }

    /// <summary>
    /// tools/multi/validate.py's "walls not closed" for the ground storey: with doors shut, the outside
    /// must not reach a floor cell. Walls are impassable pieces on the storey; floors are surfaces on it.
    /// </summary>
    private static void WallsClosed(ValidationResult r, List<MultiPart> shown, Func<ushort, StaticTiles> T, Func<MultiPart, bool> isDoor)
    {
        var storey = shown.Where(p => Stories.StoryOf(p.Z) == 0).ToList();
        var walls = new HashSet<(int, int)>(storey.Where(p => T(p.Id).IsImpassable && T(p.Id).Height >= 10 || isDoor(p)).Select(p => ((int)p.X, (int)p.Y)));
        var floors = new HashSet<(int, int)>(storey.Where(p => T(p.Id).IsSurface && !T(p.Id).IsImpassable).Select(p => ((int)p.X, (int)p.Y)));
        if (walls.Count == 0 || floors.Count == 0)
        {
            return;
        }

        var inner = floors.Where(c => !walls.Contains(c)).ToList();
        int x0 = storey.Min(p => p.X) - 1, y0 = storey.Min(p => p.Y) - 1, x1 = storey.Max(p => p.X) + 1, y1 = storey.Max(p => p.Y) + 1;
        var seen = new HashSet<(int, int)>();
        var stack = new Stack<(int, int)>();
        for (int x = x0; x <= x1; x++)
        {
            foreach (int y in new[] { y0, y1 })
            {
                if (seen.Add((x, y)))
                {
                    stack.Push((x, y));
                }
            }
        }

        for (int y = y0; y <= y1; y++)
        {
            foreach (int x in new[] { x0, x1 })
            {
                if (seen.Add((x, y)))
                {
                    stack.Push((x, y));
                }
            }
        }

        while (stack.Count > 0)
        {
            (int x, int y) = stack.Pop();
            foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx >= x0 && nx <= x1 && ny >= y0 && ny <= y1 && !walls.Contains((nx, ny)) && seen.Add((nx, ny)))
                {
                    stack.Push((nx, ny));
                }
            }
        }

        var leaks = inner.Where(seen.Contains).OrderBy(c => c.Item1).ThenBy(c => c.Item2).ToList();
        if (leaks.Count > 0)
        {
            // Only a floor the walls were meant to close: a multi that is all floor (a deck) has no walls.
            (int lx, int ly) = leaks[0];
            r.Findings.Add(new Finding("walls-open", FindingSeverity.Warning, -1, lx, ly, Stories.FloorZ,
                $"walls not closed: the outside reaches {leaks.Count} floor cell(s), e.g. {lx},{ly}"));
        }
    }
}
#endif
