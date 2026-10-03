using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Passes.Rooms;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class MazeStampParams
{
    [TunableDisplay("Corridor width (tiles)")]
    [TunableRange(1, 9)]
    public int CorridorWidth { get; set; } = 3;

    [TunableDisplay("Wall thickness (tiles)")]
    [TunableRange(1, 4)]
    public int WallThickness { get; set; } = 1;

    [TunableDisplay("Wall corner/post static ID", Tooltip = "Used where walls meet, end or are thicker than one tile. Felucca's stone set: 0x0080 corner, 0x0082 north-south run, 0x0081 east-west run.")]
    public int WallId { get; set; } = 0x0080;

    [TunableDisplay("Wall north-south run static ID")]
    public int WallIdNS { get; set; } = 0x0082;

    [TunableDisplay("Wall east-west run static ID")]
    public int WallIdEW { get; set; } = 0x0081;

    [TunableDisplay("Marble floor static ID")]
    public int FloorId { get; set; } = 0x0513;

    [TunableDisplay("Maze seed (0 = use pipeline seed)")]
    public int MazeSeed { get; set; } = 0;

    [TunableDisplay("Flatten Z to floor level")]
    public bool FlattenZ { get; set; } = true;

    [TunableDisplay("Floor Z value")]
    public int FloorZ { get; set; } = 0;

    [TunableDisplay("Place marble floor on corridors")]
    public bool PlaceFloor { get; set; } = true;
}

// Recursive-backtracker maze of stone-wall + marble-floor (0x0513) statics. Wall pieces
// follow the wall direction (see WallOrientation): runs along Y use WallIdNS, runs along X
// WallIdEW, corners/junctions/ends the corner piece.
// Operates over the entire IR scope; default 3-wide corridors with 1-tile walls
// gives a grid of (scope/4) cells. Walls are placed at Z=FloorZ on top of whatever
// terrain ImageImportPass produced — pair with a flat painter (paint-maze.ps1).
//
// This pass is disabled by default — opt in via preset (maze.preset.json) or
// the MapGen window. Generates one static per tile in scope so a 512x512 scope
// yields ~260k statics; keep scopes reasonable.
public sealed class MazeStampPass : IGenerationPass
{
    public string Name => "Maze Stamp";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Height;
    public IrFields Writes => IrFields.StaticOps | IrFields.Height;

    public object CreateDefaultParams() => new MazeStampParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MazeStampParams)parameters;
        var ir = ctx.IR;
        var scope = ir.Scope;
        int x1 = scope.X1, y1 = scope.Y1;
        int w = scope.X2 - scope.X1 + 1;
        int h = scope.Y2 - scope.Y1 + 1;

        int cell = Math.Max(1, p.CorridorWidth);
        int wall = Math.Max(1, p.WallThickness);
        int stride = cell + wall;

        // Logical maze grid: each cell occupies (cell+wall) tiles; +wall trailing border.
        int gw = (w - wall) / stride;
        int gh = (h - wall) / stride;
        if (gw < 2 || gh < 2)
        {
            ctx.Report.Warnings.Add($"maze scope too small: {w}x{h} -> grid {gw}x{gh}");
            return;
        }

        var visited = new bool[gw * gh];
        var openE = new bool[gw * gh]; // east-facing wall open between (cx,cy) and (cx+1,cy)
        var openS = new bool[gw * gh]; // south-facing wall open between (cx,cy) and (cx,cy+1)

        var rng = p.MazeSeed != 0 ? new Random(p.MazeSeed) : ctx.Rng;
        var stack = new Stack<(int cx, int cy)>();
        stack.Push((0, 0));
        visited[0] = true;

        Span<int> dirs = stackalloc int[4];
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Peek();
            dirs[0] = 0; dirs[1] = 1; dirs[2] = 2; dirs[3] = 3;
            for (int i = 3; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }

            bool moved = false;
            for (int i = 0; i < 4; i++)
            {
                int nx = cx, ny = cy;
                switch (dirs[i]) { case 0: ny--; break; case 1: nx++; break; case 2: ny++; break; case 3: nx--; break; }
                if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                int nIdx = ny * gw + nx;
                if (visited[nIdx]) continue;
                visited[nIdx] = true;
                int cIdx = cy * gw + cx;
                switch (dirs[i])
                {
                    case 0: openS[nIdx] = true; break;       // north neighbor: open the wall south of it
                    case 1: openE[cIdx] = true; break;       // east neighbor:  open the wall east of current
                    case 2: openS[cIdx] = true; break;       // south neighbor: open the wall south of current
                    case 3: openE[nIdx] = true; break;       // west neighbor:  open the wall east of it
                }
                stack.Push((nx, ny));
                moved = true;
                break;
            }
            if (!moved) stack.Pop();
        }

        if (p.FlattenZ) ir.EnsureHeight();

        sbyte floorZ = (sbyte)Math.Clamp(p.FloorZ, sbyte.MinValue, sbyte.MaxValue);
        ushort floorId = (ushort)p.FloorId;

        // Pass 1: wall mask (so each wall tile can look at its neighbours).
        var wallMask = new bool[w * h];
        for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx++)
        {
            int rx = xx % stride;
            int ry = yy % stride;
            int gx = xx / stride;
            int gy = yy / stride;

            bool isWall;
            if (gx >= gw || gy >= gh)
            {
                isWall = true;
            }
            else if (rx < wall && ry < wall)
            {
                isWall = true; // corner pillar
            }
            else if (rx < wall)
            {
                // West-side wall column of cell (gx, gy)
                if (gx == 0) isWall = true;
                else isWall = !openE[gy * gw + (gx - 1)];
            }
            else if (ry < wall)
            {
                // North-side wall row of cell (gx, gy)
                if (gy == 0) isWall = true;
                else isWall = !openS[(gy - 1) * gw + gx];
            }
            else
            {
                isWall = false;
            }
            wallMask[yy * w + xx] = isWall;
        }
        bool WallAt(int xx, int yy) => xx >= 0 && yy >= 0 && xx < w && yy < h && wallMask[yy * w + xx];

        // Pass 2: emit.
        int walls = 0, floors = 0;
        for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx++)
        {
            bool isWall = wallMask[yy * w + xx];
            int absX = x1 + xx;
            int absY = y1 + yy;
            int idx = ir.Index(absX, absY);
            sbyte z = floorZ;
            if (p.FlattenZ && ir.Height_Z is not null) ir.Height_Z[idx] = floorZ;
            else if (ir.Height_Z is not null) z = ir.Height_Z[idx];

            if (isWall)
            {
                ushort wallId = WallOrientation.Pick(
                    WallAt(xx, yy - 1), WallAt(xx, yy + 1), WallAt(xx + 1, yy), WallAt(xx - 1, yy),
                    p.WallId, p.WallIdNS, p.WallIdEW);
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)absX, (ushort)absY, z, wallId, 0));
                walls++;
            }
            else if (p.PlaceFloor)
            {
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)absX, (ushort)absY, z, floorId, 0));
                floors++;
            }
        }

        ctx.Report.StaticsAdded += walls + floors;
        ctx.Report.TilesTouched += w * h;
        ctx.Report.Notes.Add($"maze grid={gw}x{gh} cell={cell} wall={wall} walls={walls} floors={floors}");
    }
}
