#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Game.GameObjects;
using GUO.Game.Map;

/// <summary>What an object on a cell is, for the Types mode (tiledata flags first, then the tile's name).</summary>
internal enum Kind
{
    Land, Floor, Wall, Window, Door, Roof, Stairs, Foliage, Water, Prop,
}

/// <summary>What the client's movement rules make of a cell.</summary>
internal enum Walk : byte
{
    Blocked, Walkable, Surface, Wet, Unknown = 255,
}

[Flags]
internal enum Problem
{
    None = 0,
    Hole = 1,
    ZFight = 2,
    OnWater = 4,
}

/// <summary>One static, multi part or placed item on a cell, as the modes read it.</summary>
internal struct ObjInfo
{
    public ushort Graphic;
    public sbyte Z;
    public byte Height;
    public TileFlag Flags;
    public bool IsItem;
    public bool IsMulti;
    public Kind Kind;
    public string Name;

    public int Top => Z + Height;
    public bool Surface => (Flags & TileFlag.Surface) != 0;
    public bool Wet => (Flags & TileFlag.Wet) != 0;
    public bool Bridge => (Flags & TileFlag.Bridge) != 0;
}

/// <summary>One 8x8 block as the modes see it: land and objects per cell, and what is computed from them.</summary>
internal sealed class BlockData
{
    public readonly int Bx, By;
    public readonly ushort[] LandId = new ushort[64];
    public readonly sbyte[] LandZ = new sbyte[64];
    public readonly sbyte[] LandMin = new sbyte[64];
    public readonly sbyte[] LandAvg = new sbyte[64];
    public readonly bool[] Stretched = new bool[64];
    public readonly bool[] LandWet = new bool[64];
    public readonly List<ObjInfo>[] Objs = new List<ObjInfo>[64];

    // Computed on demand.
    public readonly Walk[] Walks = new Walk[64];
    public readonly sbyte[][] Standable = new sbyte[64][];
    public readonly Problem[] Problems = new Problem[64];
    public readonly bool[] ProblemsKnown = new bool[64];

    public BlockData(int bx, int by)
    {
        Bx = bx;
        By = by;
        Array.Fill(Walks, Walk.Unknown);
    }
}

/// <summary>
/// The cell data the render modes read (ADR-0027): the world's loaded
/// chunks, copied per 8x8 block into plain arrays and kept until an edit or a
/// reload changes the world. Heavy answers (the client's walkability, problems)
/// are computed per cell on first use and kept in the same block.
/// </summary>
internal sealed class WorldData
{
    private readonly WorldHost _host;
    private readonly Dictionary<long, BlockData> _blocks = new();
    private int _facet = -1;

    public WorldData(WorldHost host)
    {
        _host = host;
    }

    /// <summary>Bumped on every <see cref="Invalidate"/>, so a layer or mode can drop what it cached from this data.</summary>
    public int Version { get; private set; }

    public int BlocksCached => _blocks.Count;

    public WorldHost Host => _host;

    /// <summary>Forgets every block (an edit, a reload, a facet change).</summary>
    public void Invalidate()
    {
        _blocks.Clear();
        Version++;
    }

    private static long Key(int bx, int by) => ((long)bx << 20) | (uint)by;

    /// <summary>The block, loading it into the world if it is not loaded; null outside the map.</summary>
    public BlockData Block(int bx, int by)
    {
        GUO.Game.World world = _host.World;
        if (world?.Map == null || bx < 0 || by < 0)
        {
            return null;
        }

        if (world.MapIndex != _facet)
        {
            _facet = world.MapIndex;
            Invalidate();
        }

        long key = Key(bx, by);
        if (_blocks.TryGetValue(key, out BlockData b))
        {
            return b;
        }

        Chunk chunk = world.Map.GetChunk2(bx, by, load: true);
        if (chunk == null)
        {
            _blocks[key] = null;
            return null;
        }

        b = Build(chunk, bx, by);
        _blocks[key] = b;
        return b;
    }

    private static TileDataLoader Tiles => GUO.Client.Game.UO.FileManager.TileData;

    private static BlockData Build(Chunk chunk, int bx, int by)
    {
        var b = new BlockData(bx, by);
        TileDataLoader tiles = Tiles;
        for (int cx = 0; cx < 8; cx++)
        {
            for (int cy = 0; cy < 8; cy++)
            {
                int i = (cy << 3) + cx;
                for (GameObject o = chunk.GetHeadObject(cx, cy); o != null; o = o.TNext)
                {
                    switch (o)
                    {
                        case Land land:
                            b.LandId[i] = land.Graphic;
                            b.LandZ[i] = land.Z;
                            b.LandMin[i] = land.MinZ;
                            b.LandAvg[i] = land.AverageZ;
                            b.Stretched[i] = land.IsStretched;
                            b.LandWet[i] = land.Graphic < tiles.LandData.Length && tiles.LandData[land.Graphic].IsWet;
                            break;
                        case Static or Multi:
                            Add(b, i, o.Graphic, o.Z, tiles, false, o is Multi);
                            break;
                        case Item it when !it.IsMulti && it.OnGround:
                            Add(b, i, it.Graphic, it.Z, tiles, true, false);
                            break;
                    }
                }
            }
        }

        return b;
    }

    private static void Add(BlockData b, int i, ushort graphic, sbyte z, TileDataLoader tiles, bool item, bool multi)
    {
        if (graphic >= tiles.StaticData.Length)
        {
            return;
        }

        ref StaticTiles t = ref tiles.StaticData[graphic];
        (b.Objs[i] ??= new List<ObjInfo>(2)).Add(new ObjInfo
        {
            Graphic = graphic,
            Z = z,
            Height = t.Height,
            Flags = t.Flags,
            IsItem = item,
            IsMulti = multi,
            Name = t.Name ?? "",
            Kind = Classify(t.Flags, t.Height, t.Name ?? ""),
        });
    }

    /// <summary>Tiledata flags first, then the tile's name (the client's data names its art).</summary>
    public static Kind Classify(TileFlag flags, int height, string name)
    {
        string n = name.ToLowerInvariant();
        if ((flags & TileFlag.Door) != 0 || n.Contains("door"))
        {
            return Kind.Door;
        }

        if ((flags & TileFlag.Window) != 0 || n.Contains("window"))
        {
            return Kind.Window;
        }

        if ((flags & TileFlag.Bridge) != 0 || n.Contains("stair") || n.Contains("ramp"))
        {
            return Kind.Stairs;
        }

        if ((flags & TileFlag.Roof) != 0 || n.Contains("roof"))
        {
            return Kind.Roof;
        }

        if ((flags & TileFlag.Wall) != 0 || n.Contains("wall"))
        {
            return Kind.Wall;
        }

        if ((flags & TileFlag.Foliage) != 0 || n.Contains("tree") || n.Contains("leaves") || n.Contains("bush"))
        {
            return Kind.Foliage;
        }

        if ((flags & TileFlag.Wet) != 0 || n.Contains("water"))
        {
            return Kind.Water;
        }

        bool surface = (flags & TileFlag.Surface) != 0;
        if (n.Contains("floor") || n.Contains("flagstone") || n.Contains("carpet") || n.Contains("pavement")
            || (surface && height == 0))
        {
            return Kind.Floor;
        }

        return Kind.Prop;
    }

    // --- lookups ----------------------------------------------------------

    private BlockData Of(int x, int y) => x < 0 || y < 0 ? null : Block(x >> 3, y >> 3);

    private static int Ix(int x, int y) => ((y & 7) << 3) + (x & 7);

    /// <summary>The land height at a cell (what the game draws a corner at); 0 outside the map.</summary>
    public sbyte LandZ(int x, int y)
    {
        BlockData b = Of(x, y);
        return b == null ? (sbyte)0 : b.LandZ[Ix(x, y)];
    }

    public bool InMap(int x, int y) => Of(x, y) != null;

    public ushort LandId(int x, int y)
    {
        BlockData b = Of(x, y);
        return b == null ? (ushort)0 : b.LandId[Ix(x, y)];
    }

    public bool Stretched(int x, int y)
    {
        BlockData b = Of(x, y);
        return b != null && b.Stretched[Ix(x, y)];
    }

    public bool LandWet(int x, int y)
    {
        BlockData b = Of(x, y);
        return b != null && b.LandWet[Ix(x, y)];
    }

    private static readonly List<ObjInfo> None = new();

    /// <summary>The statics, multi parts and placed items on a cell.</summary>
    public List<ObjInfo> Objects(int x, int y)
    {
        BlockData b = Of(x, y);
        return b?.Objs[Ix(x, y)] ?? None;
    }

    /// <summary>The land height, raised to the top of the highest surface static (floors, steps, tables) on the cell.</summary>
    public int SurfaceZ(int x, int y)
    {
        int z = LandZ(x, y);
        foreach (ObjInfo o in Objects(x, y))
        {
            if (!o.IsItem && o.Surface && !o.Wet)
            {
                z = Math.Max(z, o.Top);
            }
        }

        return z;
    }

    /// <summary>What a cell is, for the Types mode: the highest ranked kind on it, else land or water.</summary>
    public Kind KindAt(int x, int y)
    {
        Kind best = LandWet(x, y) ? Kind.Water : Kind.Land;
        int bestRank = -1;
        foreach (ObjInfo o in Objects(x, y))
        {
            if (o.IsItem)
            {
                continue;
            }

            int r = Rank(o.Kind);
            if (r > bestRank)
            {
                bestRank = r;
                best = o.Kind;
            }
        }

        return best;
    }

    private static int Rank(Kind k) => k switch
    {
        Kind.Door => 9,
        Kind.Window => 8,
        Kind.Wall => 7,
        Kind.Stairs => 6,
        Kind.Roof => 5,
        Kind.Foliage => 4,
        Kind.Prop => 3,
        Kind.Floor => 2,
        Kind.Water => 1,
        _ => 0,
    };

    // --- walkability: the client's own rules -------------------------------

    private GUO.Game.Pathfinder Pathfinder => _host.World?.Player?.Pathfinder;

    /// <summary>
    /// The heights a character could stand at on a cell, by asking the
    /// client's own <c>Pathfinder.CalculateNewZ</c> (the check <c>CanWalk</c>
    /// makes for every step) from each height the cell offers: the land's
    /// heights and the top of each object. Empty when nothing can be stood on.
    /// </summary>
    public sbyte[] Standable(int x, int y)
    {
        BlockData b = Of(x, y);
        if (b == null)
        {
            return Array.Empty<sbyte>();
        }

        int i = Ix(x, y);
        if (b.Standable[i] != null)
        {
            return b.Standable[i];
        }

        var result = new List<sbyte>(2);
        GUO.Game.Pathfinder pf = Pathfinder;
        if (pf != null)
        {
            var candidates = new List<int> { b.LandZ[i], b.LandAvg[i], b.LandMin[i] };
            foreach (ObjInfo o in Objects(x, y))
            {
                candidates.Add(o.Z);
                candidates.Add(o.Top);
                candidates.Add(o.Z + o.Height / 2);
            }

            // CalculateNewZ looks at the cell behind the step for stretched land.
            _host.World.Map.GetChunk(x, y + 1, load: true);
            var tried = new HashSet<int>();
            foreach (int c in candidates)
            {
                if (c < -128 || c > 127 || !tried.Add(c) || tried.Count > 12)
                {
                    continue;
                }

                sbyte z = (sbyte)c;
                if (pf.CalculateNewZ(x, y, ref z, 0) && !result.Contains(z))
                {
                    result.Add(z);
                }
            }
        }

        return b.Standable[i] = result.ToArray();
    }

    /// <summary>
    /// Walkable: the ground can be stood on. Surface: only a static can
    /// (a floor, a bridge, steps). Wet: water. Blocked: nothing can be stood on.
    /// </summary>
    public Walk WalkAt(int x, int y)
    {
        BlockData b = Of(x, y);
        if (b == null)
        {
            return Walk.Blocked;
        }

        int i = Ix(x, y);
        if (b.Walks[i] != Walk.Unknown)
        {
            return b.Walks[i];
        }

        sbyte[] std = Standable(x, y);
        Walk w;
        if (std.Length == 0)
        {
            bool wet = b.LandWet[i];
            foreach (ObjInfo o in Objects(x, y))
            {
                wet |= o.Wet;
            }

            w = wet ? Walk.Wet : Walk.Blocked;
        }
        else
        {
            w = Walk.Surface;
            foreach (sbyte z in std)
            {
                if (z >= b.LandMin[i] - 1 && z <= b.LandZ[i] + 1 || Math.Abs(z - b.LandAvg[i]) <= 1)
                {
                    w = Walk.Walkable;
                }
            }
        }

        return b.Walks[i] = w;
    }

    /// <summary>The walkability if already worked out, else <see cref="Walk.Unknown"/>.</summary>
    public Walk PeekWalk(int x, int y)
    {
        BlockData b = Of(x, y);
        return b == null ? Walk.Blocked : b.Walks[Ix(x, y)];
    }

    // --- problems ---------------------------------------------------------

    private static bool Floorish(List<ObjInfo> objs)
    {
        foreach (ObjInfo o in objs)
        {
            if (!o.IsItem && (o.Kind is Kind.Floor or Kind.Stairs or Kind.Door))
            {
                return true;
            }
        }

        return false;
    }

    private bool FloorAt(int x, int y) => InMap(x, y) && Floorish(Objects(x, y));

    /// <summary>
    /// What is wrong on a cell. A hole: no floor on the cell while the cells
    /// either side of it (north and south, or east and west) both have one,
    /// and nothing built stands on it. A z-fight: two statics (not placed
    /// items) on the cell at the same z that draw over each other: the same
    /// graphic twice, or two floors (a wall's two faces share a z and are not one). On water: a static that is neither
    /// water nor a surface standing on water land.
    /// </summary>
    public Problem ProblemsAt(int x, int y)
    {
        BlockData b = Of(x, y);
        if (b == null)
        {
            return Problem.None;
        }

        int i = Ix(x, y);
        if (b.ProblemsKnown[i])
        {
            return b.Problems[i];
        }

        Problem p = Problem.None;
        List<ObjInfo> objs = Objects(x, y);
        for (int a = 0; a < objs.Count; a++)
        {
            if (objs[a].IsItem)
            {
                continue;
            }

            for (int c = a + 1; c < objs.Count; c++)
            {
                if (!objs[c].IsItem && objs[c].Z == objs[a].Z
                    && (objs[c].Graphic == objs[a].Graphic || (objs[c].Kind == Kind.Floor && objs[a].Kind == Kind.Floor)))
                {
                    p |= Problem.ZFight;
                }
            }

            if (b.LandWet[i] && !objs[a].Wet && !objs[a].Surface && !objs[a].Bridge && objs[a].Z <= b.LandZ[i] + 2)
            {
                p |= Problem.OnWater;
            }
        }

        if (!Floorish(objs) && objs.TrueForAll(o => o.IsItem || o.Kind is Kind.Prop or Kind.Foliage)
            && ((FloorAt(x - 1, y) && FloorAt(x + 1, y)) || (FloorAt(x, y - 1) && FloorAt(x, y + 1))))
        {
            p |= Problem.Hole;
        }

        b.Problems[i] = p;
        b.ProblemsKnown[i] = true;
        return p;
    }

    /// <summary>The graphic and name of everything on a cell, one per line, for a hover.</summary>
    public string Describe(int x, int y)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{x},{y}  land 0x{LandId(x, y):X4} z {LandZ(x, y)}");
        foreach (ObjInfo o in Objects(x, y))
        {
            sb.Append($"\n{o.Name} · 0x{o.Graphic:X4}  z {o.Z}+{o.Height}{(o.IsItem ? " (item)" : "")}");
        }

        return sb.ToString();
    }
}
#endif
