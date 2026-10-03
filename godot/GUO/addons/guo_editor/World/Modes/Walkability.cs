#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using GUO.Game.Data;

/// <summary>
/// Steps over the map the way the client's pathfinder does: every step is
/// <c>Pathfinder.CanWalk</c> (ADR-0027), so what is reachable here is what a
/// player of the client could reach. Shared by the flood fill and the route.
/// </summary>
internal static class WalkRules
{
    /// <summary>The cells one step from (x, y, z), by the client's rules.</summary>
    public static IEnumerable<(int X, int Y, sbyte Z)> Steps(GUO.Game.Pathfinder pf, int x, int y, sbyte z)
    {
        for (int dir = 0; dir < 8; dir++)
        {
            var d = (Direction)dir;
            int nx = x, ny = y;
            sbyte nz = z;
            if (pf.CanWalk(ref d, ref nx, ref ny, ref nz))
            {
                yield return (nx, ny, nz);
            }
        }
    }

    public static long Key(int x, int y, sbyte z) => ((long)x << 32) | ((long)y << 8) | (byte)(z + 128);

    /// <summary>Snaps a standing height to one the client allows on the cell, or null.</summary>
    public static sbyte? Snap(GUO.Game.Pathfinder pf, int x, int y, sbyte z)
    {
        sbyte s = z;
        return pf.CalculateNewZ(x, y, ref s, 0) ? s : null;
    }
}

/// <summary>
/// Flood fill from a cell by the client's walking rules, within a radius.
/// Time sliced (<see cref="Step"/>), and kept per 8x8 block as a bit mask of
/// the cells reached, so drawing it is a lookup.
/// </summary>
internal sealed class ReachFill
{
    private readonly GUO.Game.Pathfinder _pf;
    private readonly Queue<(int X, int Y, sbyte Z)> _queue = new();
    private readonly HashSet<long> _seen = new();
    private readonly Dictionary<long, ulong> _masks = new();

    public readonly int OriginX, OriginY, Radius;

    /// <summary>False when the origin cannot be stood on; then nothing is reached.</summary>
    public bool Valid { get; }
    public bool Done => !Valid || _queue.Count == 0;
    public int Count { get; private set; }

    public ReachFill(WorldData data, int x, int y, sbyte z, int radius = 32)
    {
        _pf = data.Host.World.Player.Pathfinder;
        OriginX = x;
        OriginY = y;
        Radius = radius;
        sbyte? start = WalkRules.Snap(_pf, x, y, z);
        Valid = start != null;
        if (Valid)
        {
            Visit(x, y, start.Value);
        }
    }

    private void Visit(int x, int y, sbyte z)
    {
        if (Math.Abs(x - OriginX) > Radius || Math.Abs(y - OriginY) > Radius || !_seen.Add(WalkRules.Key(x, y, z)))
        {
            return;
        }

        long bk = ((long)(x >> 3) << 20) | (uint)(y >> 3);
        int bit = ((y & 7) << 3) + (x & 7);
        _masks.TryGetValue(bk, out ulong m);
        if ((m & (1UL << bit)) == 0)
        {
            Count++;
        }

        _masks[bk] = m | (1UL << bit);
        _queue.Enqueue((x, y, z));
    }

    /// <summary>Spends up to <paramref name="ms"/> milliseconds filling; true when finished.</summary>
    public bool Step(double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_queue.Count > 0 && sw.Elapsed.TotalMilliseconds < ms)
        {
            var (x, y, z) = _queue.Dequeue();
            foreach (var n in WalkRules.Steps(_pf, x, y, z))
            {
                Visit(n.X, n.Y, n.Z);
            }
        }

        return Done;
    }

    public bool Reached(int x, int y)
    {
        long bk = ((long)(x >> 3) << 20) | (uint)(y >> 3);
        return _masks.TryGetValue(bk, out ulong m) && (m & (1UL << (((y & 7) << 3) + (x & 7)))) != 0;
    }

    /// <summary>Runs to the end (the smoke check, the scene pack).</summary>
    public void Finish()
    {
        while (!Step(1000))
        {
        }
    }
}

/// <summary>A* over the client's walking rules.</summary>
internal static class Route
{
    /// <summary>The steps from the start to the cell (goalX, goalY), or null if none is found within the node limit.</summary>
    public static List<(int X, int Y, sbyte Z)> Find(WorldData data, int sx, int sy, sbyte sz, int gx, int gy, int maxNodes = 30000)
    {
        GUO.Game.Pathfinder pf = data.Host.World.Player.Pathfinder;
        sbyte? start = WalkRules.Snap(pf, sx, sy, sz);
        if (start == null)
        {
            return null;
        }

        var open = new PriorityQueue<(int X, int Y, sbyte Z), int>();
        var cost = new Dictionary<long, int>();
        var from = new Dictionary<long, (int X, int Y, sbyte Z)>();
        var s = (sx, sy, start.Value);
        open.Enqueue(s, 0);
        cost[WalkRules.Key(sx, sy, start.Value)] = 0;
        int nodes = 0;
        while (open.TryDequeue(out var cur, out _) && nodes++ < maxNodes)
        {
            if (cur.X == gx && cur.Y == gy)
            {
                var path = new List<(int, int, sbyte)> { cur };
                while (from.TryGetValue(WalkRules.Key(cur.X, cur.Y, cur.Z), out var prev))
                {
                    path.Add(prev);
                    cur = prev;
                }

                path.Reverse();
                return path;
            }

            int c = cost[WalkRules.Key(cur.X, cur.Y, cur.Z)];
            foreach (var n in WalkRules.Steps(pf, cur.X, cur.Y, cur.Z))
            {
                long k = WalkRules.Key(n.X, n.Y, n.Z);
                if (cost.TryGetValue(k, out int old) && old <= c + 1)
                {
                    continue;
                }

                cost[k] = c + 1;
                from[k] = cur;
                open.Enqueue(n, c + 1 + Math.Max(Math.Abs(gx - n.X), Math.Abs(gy - n.Y)));
            }
        }

        return null;
    }
}
#endif
