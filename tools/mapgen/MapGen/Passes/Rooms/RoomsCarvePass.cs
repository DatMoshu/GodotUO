using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Passes.Rooms;

public sealed class RoomsCarveParams
{
    [TunableDisplay("BSP max leaf size (tiles)")]
    [TunableRange(8, 80)]
    public int MaxLeafSize { get; set; } = 22;

    [TunableDisplay("BSP min leaf size (tiles)")]
    [TunableRange(5, 40)]
    public int MinLeafSize { get; set; } = 9;

    [TunableDisplay("Room min size (tiles, square edge)")]
    [TunableRange(3, 40)]
    public int RoomMin { get; set; } = 5;

    [TunableDisplay("Room max size (tiles, square edge)")]
    [TunableRange(4, 80)]
    public int RoomMax { get; set; } = 20;

    [TunableDisplay("Leaf margin around room (tiles)")]
    [TunableRange(1, 4)]
    public int LeafMargin { get; set; } = 1;

    [TunableDisplay("Tunnel half-width (tiles)")]
    [TunableRange(0, 4)]
    public int TunnelHalfWidth { get; set; } = 1;

    [TunableDisplay("Carve seed (0 = use pipeline seed)")]
    public int CarveSeed { get; set; } = 0;

    [TunableDisplay("Weight: Trash rooms")]
    public int WeightTrash { get; set; } = 5;
    [TunableDisplay("Weight: Elite rooms")]
    public int WeightElite { get; set; } = 2;
    [TunableDisplay("Weight: Boss rooms")]
    public int WeightBoss { get; set; } = 1;
    [TunableDisplay("Weight: Treasure rooms")]
    public int WeightTreasure { get; set; } = 1;
    [TunableDisplay("Weight: Empty rooms")]
    public int WeightEmpty { get; set; } = 1;
}

/// <summary>
/// Rogue-style dungeon carver. BSP-partitions the IR scope into leaves, drops
/// one weighted-<see cref="RoomKind"/> room per viable leaf, and connects
/// centroids with L-shaped Manhattan tunnels along a Prim's MST plus a small
/// number of bonus loop edges so the graph isn't strictly linear. Guarantees
/// at least one Boss and one Treasure room when any rooms are produced.
/// Writes <see cref="GenIR.DungeonZone"/> (dense byte field, walls included)
/// and appends <see cref="DungeonRoomRect"/> records to
/// <see cref="GenIR.DungeonRooms"/>. Does not emit any <c>StaticOp</c>s
/// directly — <c>RoomsStampPass</c> consumes the zone field for that.
/// </summary>
public sealed class RoomsCarvePass : IGenerationPass
{
    public string Name => "Rooms Carve";
    public string Category => "Rooms";

    public IrFields Reads => IrFields.None;
    public IrFields Writes => IrFields.DungeonZone;

    public object CreateDefaultParams() => new RoomsCarveParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RoomsCarveParams)parameters;
        var ir = ctx.IR;
        var scope = ir.Scope;
        ir.EnsureDungeonZone();
        var zone = ir.DungeonZone!;

        int x1 = scope.X1, y1 = scope.Y1;
        int x2 = scope.X2, y2 = scope.Y2;
        int w = x2 - x1 + 1;
        int h = y2 - y1 + 1;

        var rng = p.CarveSeed != 0 ? new Random(p.CarveSeed) : ctx.Rng;
        int roomMin = Math.Max(3, p.RoomMin);
        int roomMax = Math.Max(roomMin, p.RoomMax);
        int maxLeaf = Math.Max(roomMin + 2, p.MaxLeafSize);
        int minLeaf = Math.Max(roomMin + 2, p.MinLeafSize);
        if (minLeaf > maxLeaf) minLeaf = maxLeaf;
        int leafMargin = Math.Max(1, p.LeafMargin);

        // Initialize everything to Outside (=solid wall).
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            zone[ir.Index(x1 + x, y1 + y)] = (byte)DungeonZone.Outside;

        // ----- BSP partition the scope into leaves; one room per leaf -----
        // Keep a 1-tile inset from the scope edge so rooms never sit flush against
        // the boundary (the wall pass needs a tile to put walls in).
        var leaves = new List<(int X1, int Y1, int X2, int Y2)>();
        BspSplit(
            x1 + 1, y1 + 1, x2 - 1, y2 - 1,
            minLeaf, maxLeaf, 0, 12, rng, leaves);

        var rooms = new List<DungeonRoomRect>();
        var kindWeights = new[]
        {
            (RoomKind.Trash,    Math.Max(0, p.WeightTrash)),
            (RoomKind.Elite,    Math.Max(0, p.WeightElite)),
            (RoomKind.Boss,     Math.Max(0, p.WeightBoss)),
            (RoomKind.Treasure, Math.Max(0, p.WeightTreasure)),
            (RoomKind.Empty,    Math.Max(0, p.WeightEmpty)),
        };
        int weightTotal = kindWeights.Sum(k => k.Item2);
        if (weightTotal <= 0) weightTotal = 1;

        foreach (var leaf in leaves)
        {
            int lw = leaf.X2 - leaf.X1 + 1;
            int lh = leaf.Y2 - leaf.Y1 + 1;
            int availW = lw - 2 * leafMargin;
            int availH = lh - 2 * leafMargin;
            if (availW < roomMin || availH < roomMin) continue;

            int rw = rng.Next(roomMin, Math.Min(roomMax, availW) + 1);
            int rh = rng.Next(roomMin, Math.Min(roomMax, availH) + 1);
            int rx = rng.Next(leaf.X1 + leafMargin, leaf.X1 + leafMargin + (availW - rw) + 1);
            int ry = rng.Next(leaf.Y1 + leafMargin, leaf.Y1 + leafMargin + (availH - rh) + 1);

            RoomKind kind = PickWeighted(rng, kindWeights, weightTotal);
            rooms.Add(new DungeonRoomRect(
                (ushort)rx, (ushort)ry,
                (ushort)(rx + rw - 1), (ushort)(ry + rh - 1),
                kind, rooms.Count));
        }

        bool bossPlaced = false;
        bool treasurePlaced = false;

        // Guarantee at least one Boss + one Treasure room if any rooms exist.
        if (rooms.Count > 0)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].Kind == RoomKind.Boss) bossPlaced = true;
                if (rooms[i].Kind == RoomKind.Treasure) treasurePlaced = true;
            }
            // Pick ONE index and rewrite that room (the old code drew two different random
            // indices, overwriting one room with a copy of another's bounds). Prefer a room
            // that is not the only Treasure room, so the boss doesn't eat the treasure.
            if (!bossPlaced)
            {
                int bi = PickIndex(rng, rooms, r => r.Kind != RoomKind.Treasure)
                         ?? rng.Next(rooms.Count);
                if (rooms[bi].Kind == RoomKind.Treasure) treasurePlaced = rooms.Where((r, i) => i != bi && r.Kind == RoomKind.Treasure).Any();
                rooms[bi] = WithKind(rooms[bi], RoomKind.Boss);
            }
            if (!treasurePlaced)
            {
                if (PickIndex(rng, rooms, r => r.Kind != RoomKind.Boss) is { } ti)
                    rooms[ti] = WithKind(rooms[ti], RoomKind.Treasure);
                else
                    ctx.Report.Warnings.Add($"only {rooms.Count} room(s) and all are Boss — no Treasure room");
            }
        }

        // ----- Stamp room interiors -----
        foreach (var r in rooms)
        {
            byte z = ZoneForKind(r.Kind);
            for (int y = r.Y1; y <= r.Y2; y++)
            for (int x = r.X1; x <= r.X2; x++)
                zone[ir.Index(x, y)] = z;
        }

        // ----- Connect rooms via MST + L-tunnels -----
        int tunnelW = Math.Max(0, p.TunnelHalfWidth);
        if (rooms.Count >= 2)
        {
            // Simple Prim's MST over centroids.
            var n = rooms.Count;
            var centroids = rooms
                .Select(r => ((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2)).ToArray();
            var inMst = new bool[n];
            inMst[0] = true;
            for (int e = 0; e < n - 1; e++)
            {
                int bestI = -1, bestJ = -1;
                int bestD = int.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (!inMst[i]) continue;
                    for (int j = 0; j < n; j++)
                    {
                        if (inMst[j]) continue;
                        int dx = centroids[i].Item1 - centroids[j].Item1;
                        int dy = centroids[i].Item2 - centroids[j].Item2;
                        int d = Math.Abs(dx) + Math.Abs(dy);
                        if (d < bestD) { bestD = d; bestI = i; bestJ = j; }
                    }
                }
                if (bestJ < 0) break;
                inMst[bestJ] = true;
                CarveTunnel(zone, ir, scope, centroids[bestI], centroids[bestJ], tunnelW, rng);
            }
            // One bonus loop edge for cycles (less linear).
            int loopCount = Math.Max(1, n / 6);
            for (int k = 0; k < loopCount; k++)
            {
                int a = rng.Next(n);
                int b = rng.Next(n);
                if (a == b) continue;
                CarveTunnel(zone, ir, scope, centroids[a], centroids[b], tunnelW, rng);
            }
        }

        // ----- Wall pass: any Outside tile adjacent to a Floor becomes Wall -----
        // This gives explicit wall tiles around rooms + corridors (instead of the
        // "everything outside = wall" interpretation in the stamp pass).
        for (int y = y1; y <= y2; y++)
        for (int x = x1; x <= x2; x++)
        {
            int idx = ir.Index(x, y);
            if (zone[idx] != (byte)DungeonZone.Outside) continue;
            if (HasFloorNeighbor(zone, ir, scope, x, y))
                zone[idx] = (byte)DungeonZone.Wall;
        }

        // Publish rooms.
        ir.DungeonRooms.Clear();
        ir.DungeonRooms.AddRange(rooms);

        ctx.Report.Notes.Add($"rooms placed: {rooms.Count} (leaves: {leaves.Count})");
        ctx.Report.TilesTouched += w * h;
    }

    // BSP recursive split. Cuts a rect into two children along the longer axis
    // (or random when near-square), stops when neither side can accommodate
    // min*2+1 tiles OR the rect is already at/under maxLeafSize OR maxDepth hit.
    // Output: list of leaf rects whose w and h are all between minLeaf and
    // ~2*maxLeaf-1.
    private static void BspSplit(int x1, int y1, int x2, int y2,
        int minLeaf, int maxLeaf, int depth, int maxDepth,
        Random rng, List<(int, int, int, int)> leaves)
    {
        int w = x2 - x1 + 1;
        int h = y2 - y1 + 1;
        if (w < minLeaf || h < minLeaf) return;

        bool tooBigW = w > maxLeaf;
        bool tooBigH = h > maxLeaf;
        bool canSplitH = h >= minLeaf * 2 + 1;
        bool canSplitV = w >= minLeaf * 2 + 1;

        // Force split if the rect is bigger than maxLeaf; otherwise allow a small
        // random chance to keep splitting so leaf sizes vary.
        bool mustSplit = tooBigW || tooBigH;
        bool maySplit  = (canSplitH || canSplitV) && depth < maxDepth && rng.NextDouble() < 0.35;
        if (!mustSplit && !maySplit || (!canSplitH && !canSplitV))
        {
            leaves.Add((x1, y1, x2, y2));
            return;
        }

        bool splitH;
        if (tooBigW && !tooBigH) splitH = false;
        else if (tooBigH && !tooBigW) splitH = true;
        else if (w > h * 5 / 4) splitH = false;
        else if (h > w * 5 / 4) splitH = true;
        else splitH = canSplitH && (rng.Next(2) == 0 || !canSplitV);

        if (splitH && canSplitH)
        {
            int cut = rng.Next(minLeaf, h - minLeaf + 1);
            BspSplit(x1, y1, x2, y1 + cut - 1, minLeaf, maxLeaf, depth + 1, maxDepth, rng, leaves);
            BspSplit(x1, y1 + cut, x2, y2,     minLeaf, maxLeaf, depth + 1, maxDepth, rng, leaves);
        }
        else if (canSplitV)
        {
            int cut = rng.Next(minLeaf, w - minLeaf + 1);
            BspSplit(x1, y1, x1 + cut - 1, y2, minLeaf, maxLeaf, depth + 1, maxDepth, rng, leaves);
            BspSplit(x1 + cut, y1, x2, y2,     minLeaf, maxLeaf, depth + 1, maxDepth, rng, leaves);
        }
        else
        {
            leaves.Add((x1, y1, x2, y2));
        }
    }

    private static DungeonRoomRect WithKind(DungeonRoomRect r, RoomKind k) =>
        new(r.X1, r.Y1, r.X2, r.Y2, k, r.Id);

    // Uniform random index among rooms matching the predicate, or null when none match.
    private static int? PickIndex(Random rng, List<DungeonRoomRect> rooms, Func<DungeonRoomRect, bool> pred)
    {
        var candidates = new List<int>();
        for (int i = 0; i < rooms.Count; i++) if (pred(rooms[i])) candidates.Add(i);
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private static byte ZoneForKind(RoomKind k) => k switch
    {
        RoomKind.Trash    => (byte)DungeonZone.RoomTrash,
        RoomKind.Elite    => (byte)DungeonZone.RoomElite,
        RoomKind.Boss     => (byte)DungeonZone.RoomBoss,
        RoomKind.Treasure => (byte)DungeonZone.RoomTreasure,
        RoomKind.Empty    => (byte)DungeonZone.RoomEmpty,
        _                 => (byte)DungeonZone.Floor,
    };

    private static RoomKind PickWeighted(Random rng, (RoomKind, int)[] table, int total)
    {
        int pick = rng.Next(total);
        foreach (var (k, w) in table)
        {
            pick -= w;
            if (pick < 0) return k;
        }
        return table[0].Item1;
    }

    private static void CarveTunnel(byte[] zone, GenIR ir, RectU16 scope,
        (int X, int Y) a, (int X, int Y) b, int halfWidth, Random rng)
    {
        // L-tunnel: random choice of (horiz then vert) vs (vert then horiz).
        bool horizFirst = rng.Next(2) == 0;
        int x = a.X, y = a.Y;
        int xEnd = b.X, yEnd = b.Y;
        if (horizFirst)
        {
            CarveHoriz(zone, ir, scope, x, xEnd, y, halfWidth);
            CarveVert(zone, ir, scope, y, yEnd, xEnd, halfWidth);
        }
        else
        {
            CarveVert(zone, ir, scope, y, yEnd, x, halfWidth);
            CarveHoriz(zone, ir, scope, x, xEnd, yEnd, halfWidth);
        }
    }

    private static void CarveHoriz(byte[] zone, GenIR ir, RectU16 scope, int xa, int xb, int y, int halfWidth)
    {
        int xLo = Math.Min(xa, xb), xHi = Math.Max(xa, xb);
        for (int x = xLo; x <= xHi; x++)
            for (int dy = -halfWidth; dy <= halfWidth; dy++)
                CarveTile(zone, ir, scope, x, y + dy);
    }

    private static void CarveVert(byte[] zone, GenIR ir, RectU16 scope, int ya, int yb, int x, int halfWidth)
    {
        int yLo = Math.Min(ya, yb), yHi = Math.Max(ya, yb);
        for (int y = yLo; y <= yHi; y++)
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
                CarveTile(zone, ir, scope, x + dx, y);
    }

    private static void CarveTile(byte[] zone, GenIR ir, RectU16 scope, int x, int y)
    {
        if (x < scope.X1 || x > scope.X2 || y < scope.Y1 || y > scope.Y2) return;
        int idx = ir.Index(x, y);
        // Only carve Outside → Corridor; don't overwrite room interiors.
        if (zone[idx] == (byte)DungeonZone.Outside)
            zone[idx] = (byte)DungeonZone.Corridor;
    }

    private static bool HasFloorNeighbor(byte[] zone, GenIR ir, RectU16 scope, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            int nx = x + dx, ny = y + dy;
            if (nx < scope.X1 || nx > scope.X2 || ny < scope.Y1 || ny > scope.Y2) continue;
            byte z = zone[ir.Index(nx, ny)];
            if (z != (byte)DungeonZone.Outside && z != (byte)DungeonZone.Wall) return true;
        }
        return false;
    }
}
