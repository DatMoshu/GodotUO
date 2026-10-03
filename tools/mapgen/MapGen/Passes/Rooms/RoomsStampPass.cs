using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Rooms;

public sealed class RoomsStampParams
{
    [TunableDisplay("Wall corner/post static ID", Tooltip = "Used where walls meet, end or are thicker than one tile. Felucca's stone set: 0x0080 corner, 0x0082 north-south run, 0x0081 east-west run.")]
    public int WallId { get; set; } = 0x0080;

    [TunableDisplay("Wall north-south run static ID")]
    public int WallIdNS { get; set; } = 0x0082;

    [TunableDisplay("Wall east-west run static ID")]
    public int WallIdEW { get; set; } = 0x0081;

    [TunableDisplay("Marble floor static ID")]
    public int FloorId { get; set; } = 0x0513;

    [TunableDisplay("Flatten Z to floor level")]
    public bool FlattenZ { get; set; } = true;

    [TunableDisplay("Floor Z value")]
    public int FloorZ { get; set; } = 0;

    [TunableDisplay("Place floor on corridor tiles too")]
    public bool FloorOnCorridors { get; set; } = true;
}

// Reads GenIR.DungeonZone (stamped by RoomsCarvePass) and emits one StaticOp per
// tile: a stone-wall piece for Wall zones, marble-floor (0x0513) for Room*/Corridor
// zones. Outside tiles get neither. Wall pieces follow the wall's direction: a run
// along Y uses WallIdNS, a run along X WallIdEW, and corners/junctions/ends/thick
// masses the corner piece (UO wall art is drawn per orientation; one id everywhere
// shows the wrong face on half the walls). Mirrors MazeStampPass.
public sealed class RoomsStampPass : IGenerationPass
{
    public string Name => "Rooms Stamp";
    public string Category => "Rooms";

    public IrFields Reads => IrFields.DungeonZone;
    public IrFields Writes => IrFields.StaticOps | IrFields.Height;

    public object CreateDefaultParams() => new RoomsStampParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RoomsStampParams)parameters;
        var ir = ctx.IR;
        if (ir.DungeonZone is null)
        {
            ctx.Report.Warnings.Add("DungeonZone not allocated — did RoomsCarvePass run first?");
            return;
        }
        if (p.FlattenZ) ir.EnsureHeight();

        var scope = ir.Scope;
        var zone = ir.DungeonZone;
        var height = ir.Height_Z;
        sbyte floorZ = (sbyte)Math.Clamp(p.FloorZ, sbyte.MinValue, sbyte.MaxValue);
        ushort floorId = (ushort)p.FloorId;
        bool IsWallAt(int x, int y) =>
            x >= scope.X1 && x <= scope.X2 && y >= scope.Y1 && y <= scope.Y2
            && zone[ir.Index(x, y)] == (byte)DungeonZone.Wall;

        int walls = 0, floors = 0;
        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var z = (DungeonZone)zone[idx];
            if (z == DungeonZone.Outside) continue;

            sbyte zHeight = floorZ;
            if (p.FlattenZ && height is not null) height[idx] = floorZ;
            else if (height is not null) zHeight = height[idx];

            if (z == DungeonZone.Wall)
            {
                ushort wallId = WallOrientation.Pick(
                    IsWallAt(x, y - 1), IsWallAt(x, y + 1), IsWallAt(x + 1, y), IsWallAt(x - 1, y),
                    p.WallId, p.WallIdNS, p.WallIdEW);
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)x, (ushort)y, zHeight, wallId, 0));
                walls++;
            }
            else
            {
                if (z == DungeonZone.Corridor && !p.FloorOnCorridors) continue;
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)x, (ushort)y, zHeight, floorId, 0));
                floors++;
            }
        }

        ctx.Report.StaticsAdded += walls + floors;
        ctx.Report.Notes.Add($"rooms-stamp walls={walls} floors={floors}");
    }
}

/// <summary>
/// Picks the wall piece for a wall tile from its 4-neighbour walls. Measured on Felucca's
/// stone set (0x80-0x82): 0x82 sits in north-south runs, 0x81 in east-west runs, 0x80 at
/// corners and junctions.
/// </summary>
public static class WallOrientation
{
    public static ushort Pick(bool north, bool south, bool east, bool west, int cornerId, int northSouthId, int eastWestId)
    {
        bool ns = north || south, ew = east || west;
        if (ns && !ew) return (ushort)northSouthId;
        if (ew && !ns) return (ushort)eastWestId;
        return (ushort)cornerId;
    }
}
