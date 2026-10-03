#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
//
// The sweeps below are ClassicUO's HouseCustomizationManager.ValidateDesignGrid (BSD 2-Clause, copyright
// the ClassicUO authors; the ported copy is src/Game/Managers/HouseCustomizationManager.cs), lifted off
// the game's House/Multi objects so the Multi Editor can run them on its own document (ADR-0031). The
// logic is the same on purpose: a design this grid calls legal is legal in the client's customiser.
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using GUO.Assets;
using GUO.Game.Data;

/// <summary>ClassicUO's support-propagation legality grid over a list of parts.</summary>
internal static class LegalityGrid
{
    private struct Cell
    {
        public ushort Floor, Object, Roof;
        public bool FloorLegal, RoofLegal;
        public byte ObjectLegal;
        public bool Visited, Support, FloorSupport, RoofSupport, Locked;
    }

    /// <summary>
    /// The uids of the shown parts the grid calls illegal. Empty (and <paramref name="note"/> set) when the
    /// design is outside what the client's grid covers (a plot wider than 32 cells).
    /// </summary>
    public static HashSet<int> Illegal(IReadOnlyList<MultiPart> parts, StaticTiles[] tiles, HouseTables tables, out string note)
    {
        note = null;
        var result = new HashSet<int>();
        if (parts.Count == 0 || tables == null || !tables.HasSupport)
        {
            return result;
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (MultiPart p in parts)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        int width = maxX - minX, height = maxY - minY;
        int levels = width >= 13 || height >= 13 ? 4 : 3;
        int ox = minX - 1, oy = minY - 1;
        int w = width + 2, h = height + 3;
        if (w < 1 || w > 32 || h < 1 || h > 32)
        {
            note = $"plot {width + 1}x{height + 1} is wider than the client's 32-cell grid; legality not checked";
            return result;
        }

        const int baseZ = Stories.FloorZ;
        var grid = new Cell[32, 32, levels];

        CustomHousePlaceInfo Info(ushort g) => g != 0 && tables.PlaceInfo.TryGetValue(g, out var i) ? i : null;
        int CanGoW(ushort g) => Info(g)?.CanGoW ?? 0;
        int CanGoN(ushort g) => Info(g)?.CanGoN ?? 0;
        int CanGoNWS(ushort g) => Info(g)?.CanGoNWS ?? 0;
        int DirectSup(ushort g) => Info(g)?.DirectSupports ?? 0;
        int Bottom(ushort g) => Info(g)?.Bottom ?? 0;
        static bool InGrid(int x, int y) => (uint)x < 32 && (uint)y < 32;

        int SlotOf(ushort g)
        {
            StaticTiles td = g < tiles.Length ? tiles[g] : default;
            if (td.Height < 2 || (td.Flags & TileFlag.Surface) != 0)
            {
                return 0;
            }

            return (td.Flags & TileFlag.Roof) != 0 ? 2 : 1;
        }

        bool AdjPair(ushort me, ushort other, int dx, int dy)
        {
            if (me == 0 || other == 0)
            {
                return false;
            }

            CustomHousePlaceInfo m = Info(me), o = Info(other);
            if (m == null || o == null)
            {
                return false;
            }

            if (dx < 0)
            {
                return (m.AdjUW != 0 && o.AdjUE != 0) || (m.AdjLW != 0 && o.AdjLE != 0);
            }

            if (dx > 0)
            {
                return (m.AdjUE != 0 && o.AdjUW != 0) || (m.AdjLE != 0 && o.AdjLW != 0);
            }

            if (dy < 0)
            {
                return (m.AdjUN != 0 && o.AdjUS != 0) || (m.AdjLN != 0 && o.AdjLS != 0);
            }

            return (m.AdjUS != 0 && o.AdjUN != 0) || (m.AdjLS != 0 && o.AdjLN != 0);
        }

        bool EdgeGuard(int x, int y, int l) =>
            (x != 0 || ((y != 0 || CanGoNWS(grid[0, 0, l].Object) != 0) && CanGoW(grid[0, y, l].Object) != 0))
            && (y != 0 || CanGoN(grid[x, 0, l].Object) != 0);

        foreach (MultiPart mm in parts)
        {
            if (!mm.Shown)
            {
                continue;
            }

            int gx = mm.X - ox, gy = mm.Y - oy;
            if (!InGrid(gx, gy))
            {
                continue;
            }

            int l = (mm.Z - baseZ) / 20;
            if (l < 0)
            {
                l = 0;
            }

            if (l >= levels)
            {
                continue;
            }

            switch (SlotOf(mm.Id))
            {
                case 0:
                    grid[gx, gy, l].Floor = mm.Id;
                    if (mm.Id >= 0x181D && mm.Id < 0x1829)
                    {
                        grid[gx, gy, l].Locked = true;
                    }

                    break;
                case 2:
                    grid[gx, gy, l].Roof = mm.Id;
                    break;
                default:
                    grid[gx, gy, l].Object = mm.Id;
                    break;
            }
        }

        int[] dx = { -1, 1, 0, 0 };
        int[] dy = { 0, 0, -1, 1 };

        bool Run(int x, int y, int l, int sx, int sy, bool roof)
        {
            for (int step = 1; step < 0x13; step++)
            {
                int cx = x + sx * step, cy = y + sy * step;
                if (!InGrid(cx, cy))
                {
                    return false;
                }

                ref Cell c = ref grid[cx, cy, l];
                if ((roof ? c.Roof : c.Floor) == 0)
                {
                    return false;
                }

                if (c.Support || (roof ? c.RoofSupport : c.FloorSupport))
                {
                    for (int k = 1; k <= step; k++)
                    {
                        ref Cell run = ref grid[x + sx * k, y + sy * k, l];
                        if (roof)
                        {
                            run.RoofSupport = true;
                        }
                        else
                        {
                            run.FloorSupport = true;
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        bool FloorRun(int x, int y, int l) =>
            Run(x, y, l, 0, 1, false) | Run(x, y, l, 1, 0, false) | Run(x, y, l, -1, 0, false) | Run(x, y, l, 0, -1, false);

        bool RoofRun(int x, int y, int l) =>
            Run(x, y, l, 0, 1, true) | Run(x, y, l, 1, 0, true) | Run(x, y, l, -1, 0, true) | Run(x, y, l, 0, -1, true);

        void Spread(int x, int y, int l)
        {
            grid[x, y, l].Support = true;
            ushort below = l >= 1 ? grid[x, y, l - 1].Object : (ushort)0;
            bool handled = false;

            if (InGrid(x + 1, y) && CanGoW(below) != 0)
            {
                grid[x + 1, y, l].FloorSupport = true;
                grid[x + 1, y, l].RoofSupport = true;
                handled = true;
            }

            if (!handled && InGrid(x, y + 1) && CanGoN(below) != 0)
            {
                grid[x, y + 1, l].FloorSupport = true;
                grid[x, y + 1, l].RoofSupport = true;
            }

            if (grid[x, y, l].Floor != 0)
            {
                if (InGrid(x + 1, y)) grid[x + 1, y, l].FloorSupport = true;
                if (InGrid(x - 1, y)) grid[x - 1, y, l].FloorSupport = true;
                if (InGrid(x, y + 1)) grid[x, y + 1, l].FloorSupport = true;
                if (InGrid(x, y - 1)) grid[x, y - 1, l].FloorSupport = true;
            }

            if (grid[x, y, l].Roof != 0)
            {
                for (int nx = x - 1; nx <= x + 1; nx++)
                {
                    for (int ny = y - 1; ny <= y + 1; ny++)
                    {
                        if ((nx != x || ny != y) && InGrid(nx, ny))
                        {
                            grid[nx, ny, l].RoofSupport = true;
                        }
                    }
                }
            }
        }

        for (int l = 0; l < levels; l++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    ref Cell c = ref grid[x, y, l];
                    c.Visited = false;
                    c.Support = false;
                    c.FloorSupport = false;
                    c.RoofSupport = false;
                    c.FloorLegal = false;
                    c.RoofLegal = false;
                    c.ObjectLegal = 0;
                    if (l == 0)
                    {
                        c.Support = true;
                        c.FloorSupport = true;
                        c.RoofSupport = true;
                    }
                }
            }

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (l == 0)
                    {
                        ref Cell c = ref grid[x, y, l];
                        c.Visited = true;
                        c.Support = true;
                        c.FloorSupport = true;
                        c.RoofSupport = true;
                    }
                    else
                    {
                        ref Cell below = ref grid[x, y, l - 1];
                        if (DirectSup(below.Object) != 0 && below.ObjectLegal != 0)
                        {
                            Spread(x, y, l);
                        }
                    }
                }
            }

            bool changed;
            do
            {
                changed = false;
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        ref Cell c = ref grid[x, y, l];
                        if (l == 0)
                        {
                            c.Visited = true;
                            c.Support = true;
                            c.FloorSupport = true;
                            c.RoofSupport = true;
                        }
                        else if (!c.Visited)
                        {
                            if ((!c.FloorSupport && !c.Support) || c.Floor == 0)
                            {
                                if (c.RoofSupport && c.Roof != 0)
                                {
                                    changed |= RoofRun(x, y, l);
                                    c.Visited = true;
                                }
                            }
                            else
                            {
                                changed |= FloorRun(x, y, l);
                                if (c.Roof != 0)
                                {
                                    changed |= RoofRun(x, y, l);
                                }

                                c.Visited = true;
                            }
                        }
                    }
                }
            }
            while (changed);

            for (int x = 1; x < w; x++)
            {
                for (int y = 1; y < h; y++)
                {
                    ref Cell c = ref grid[x, y, l];
                    if (c.Floor == 0)
                    {
                        continue;
                    }

                    if (l == levels - 1)
                    {
                        StaticTiles td = c.Floor < tiles.Length ? tiles[c.Floor] : default;
                        if (td.Height >= 2 && (td.Flags & TileFlag.Surface) != 0)
                        {
                            continue;
                        }
                    }

                    if (l == 0 || c.Support || c.FloorSupport)
                    {
                        c.FloorLegal = true;
                    }
                }
            }

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    ref Cell c = ref grid[x, y, l];
                    if (c.Roof != 0 && (l == 0 || c.Support || c.RoofSupport || c.FloorLegal))
                    {
                        c.RoofLegal = true;
                    }
                }
            }

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (!EdgeGuard(x, y, l))
                    {
                        continue;
                    }

                    ref Cell c = ref grid[x, y, l];
                    if (Bottom(c.Object) == 0 || c.Locked)
                    {
                        continue;
                    }

                    bool ok = c.Support || c.FloorLegal
                        || (CanGoN(c.Object) != 0 && InGrid(x, y + 1) && grid[x, y + 1, l].FloorLegal)
                        || (CanGoW(c.Object) != 0 && InGrid(x + 1, y) && grid[x + 1, y, l].FloorLegal)
                        || (CanGoNWS(c.Object) != 0 && InGrid(x + 1, y + 1) && grid[x + 1, y + 1, l].FloorLegal);
                    if (ok)
                    {
                        c.ObjectLegal = 1;
                    }
                }
            }

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (!EdgeGuard(x, y, l))
                    {
                        continue;
                    }

                    ref Cell c = ref grid[x, y, l];
                    if (c.Object == 0 || c.ObjectLegal != 0 || c.Locked)
                    {
                        continue;
                    }

                    const byte rank = 2;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + dx[d], ny = y + dy[d];
                        if (!InGrid(nx, ny))
                        {
                            continue;
                        }

                        Cell n = grid[nx, ny, l];
                        if (n.ObjectLegal != 0 && n.ObjectLegal < rank && AdjPair(c.Object, n.Object, dx[d], dy[d]))
                        {
                            c.ObjectLegal = rank;
                            break;
                        }
                    }
                }
            }
        }

        foreach (MultiPart mm in parts)
        {
            if (!mm.Shown)
            {
                continue;
            }

            int gx = mm.X - ox, gy = mm.Y - oy;
            if (!InGrid(gx, gy))
            {
                continue;
            }

            int l = (mm.Z - baseZ) / 20;
            if (l < 0)
            {
                l = 0;
            }

            if (l >= levels)
            {
                continue;
            }

            ref Cell cell = ref grid[gx, gy, l];
            if (cell.Locked)
            {
                continue;
            }

            bool legal = SlotOf(mm.Id) switch
            {
                0 => cell.FloorLegal,
                2 => cell.RoofLegal,
                _ => cell.ObjectLegal != 0,
            };
            if (!legal)
            {
                result.Add(mm.Uid);
            }
        }

        return result;
    }
}
#endif
