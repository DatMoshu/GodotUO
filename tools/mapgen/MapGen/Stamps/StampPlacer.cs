using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Stamp;

namespace CentrED.MapGen.Stamps;

/// <summary>Read-only view of the terrain a stamp is placed on. MapGen wraps a GenIR; the editor wraps its live map.</summary>
public interface IStampTerrain
{
    bool InBounds(int x, int y);
    int LandZ(int x, int y);
    ushort LandId(int x, int y);
}

/// <summary><see cref="IStampTerrain"/> over a GenIR, bounded by its scope.</summary>
public sealed class GenIrTerrain : IStampTerrain
{
    private readonly GenIR _ir;
    public GenIrTerrain(GenIR ir) { _ir = ir; }
    public bool InBounds(int x, int y) =>
        x >= _ir.Scope.X1 && y >= _ir.Scope.Y1 && x <= _ir.Scope.X2 && y <= _ir.Scope.Y2;
    public int LandZ(int x, int y) => _ir.Height_Z is { } h ? h[_ir.Index(x, y)] : 0;
    public ushort LandId(int x, int y) => _ir.LandId is { } l ? l[_ir.Index(x, y)] : (ushort)0;
}

/// <summary>How a stamp's statics are put on the ground.</summary>
public enum StampGrounding : byte
{
    /// <summary>
    /// The terrain is left alone; every static keeps its height above the ground of its own
    /// tile (a cluster member uses its cluster anchor's tile). Trees stay on the ground on
    /// slopes. For orientation-free scatter (forest).
    /// </summary>
    PerTile = 0,
    /// <summary>
    /// The stamp is placed as one rigid piece at a base Z (mean terrain Z of the footprint):
    /// its land Z relief is written to the map (the footprint's outer ring is averaged with
    /// the surrounding terrain) and statics keep their exact offsets, so buildings, roads and
    /// walls stay intact. For structures (town, road, ruin).
    /// </summary>
    Rigid = 1,
}

public sealed class StampPlaceOptions
{
    /// <summary>Footprint tiles at or below this Z count as water.</summary>
    public int SeaLevelZ = 0;
    /// <summary>Reject when max-min terrain Z across the footprint exceeds this.</summary>
    public int MaxLocalSlope = 12;
    /// <summary>Write the stamp's land ids. Only honoured for the Original variant.</summary>
    public bool PaintLand = false;
    public StampGrounding Grounding = StampGrounding.PerTile;
    /// <summary>Added to every static Z (editor Z-offset slider).</summary>
    public int ZOffset = 0;
    /// <summary>Reject footprints that touch water tiles or tiles at/below sea level.</summary>
    public bool RejectWater = true;
    /// <summary>When set, every footprint tile's biome must be in this set (not just the anchor).</summary>
    public IReadOnlySet<BiomeId>? AllowedBiomes;
    /// <summary>GenIR placement only: respect and update the shared occupancy grid.</summary>
    public bool UseOccupancy = true;
    /// <summary>GenIR placement only: remove scatter statics (soft cells) under the footprint instead of rejecting.</summary>
    public bool ClearSoftStatics = true;
    /// <summary>Rigid only: average the footprint's outer ring with the terrain to avoid a Z cliff at the seam.</summary>
    public bool BlendEdges = true;
    /// <summary>Label recorded with the placement (pass name), for validation reports.</summary>
    public string Source = "stamp";
    /// <summary>
    /// Never carry water onto dry land: a stamp's water land ids keep the terrain's id, and
    /// its water statics (shore foam, canal water) are dropped on cells that are not water.
    /// Town and road windows mined next to rivers and coasts otherwise paste water patches
    /// into meadows.
    /// </summary>
    public bool KeepDryLand = true;
}

public readonly record struct PlannedLand(int X, int Y, ushort Id, sbyte Z);
public readonly record struct PlannedStatic(int X, int Y, sbyte Z, ushort Id, ushort Hue);

/// <summary>The result of <see cref="StampPlacer.Plan"/>: world-space edits, or why the spot was refused.</summary>
public sealed class StampPlan
{
    public string? RejectReason;
    public bool Ok => RejectReason is null;
    /// <summary>Variant actually used (Original when the stamp's kind does not allow variants).</summary>
    public StampVariant Variant;
    public int BaseZ;
    public List<(int X, int Y)> Footprint { get; } = new();
    /// <summary>Land edits (empty unless Rigid grounding or PaintLand).</summary>
    public List<PlannedLand> Land { get; } = new();
    public List<PlannedStatic> Statics { get; } = new();

    internal StampPlan Reject(string why) { RejectReason = why; return this; }
}

/// <summary>
/// Shared stamp placement: one implementation for the MapGen passes and the editor.
/// <see cref="Plan"/> is pure (terrain in, edits out); <see cref="TryPlace"/> applies a plan
/// to a GenIR with occupancy, land-dirty and ground bookkeeping.
/// </summary>
public static class StampPlacer
{
    /// <summary>
    /// Computes where every land tile and static of <paramref name="stamp"/> goes when its
    /// anchor is put on (<paramref name="cx"/>, <paramref name="cy"/>). Checks bounds, water,
    /// biome and slope over the whole footprint. Never mutates anything.
    /// </summary>
    public static StampPlan Plan(LoadedStamp stamp, int cx, int cy, StampVariant variant,
        IStampTerrain terrain, StampPlaceOptions opt, Func<int, int, BiomeId>? biomeAt = null)
    {
        var plan = new StampPlan { Variant = StampVariantPolicy.Resolve(stamp, variant) };
        var v = plan.Variant;

        // ---- footprint ----
        if (stamp.LandCount > 0)
        {
            for (int i = 0; i < stamp.LandCount; i++)
            {
                var (rx, ry) = StampTransform.Apply(stamp.LandRx[i], stamp.LandRy[i], v);
                plan.Footprint.Add((cx + rx, cy + ry));
            }
        }
        else
        {
            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < stamp.StaticCount; i++)
            {
                var c = StaticCell(stamp, i, cx, cy, v);
                if (seen.Add(c)) plan.Footprint.Add(c);
            }
        }
        if (plan.Footprint.Count == 0) return plan.Reject("empty stamp");

        int minZ = int.MaxValue, maxZ = int.MinValue;
        long sumZ = 0;
        foreach (var (gx, gy) in plan.Footprint)
        {
            if (!terrain.InBounds(gx, gy)) return plan.Reject("out of bounds");
            int z = terrain.LandZ(gx, gy);
            if (opt.RejectWater && (z <= opt.SeaLevelZ || TileFlags.IsWaterLandId(terrain.LandId(gx, gy))))
                return plan.Reject("water");
            if (opt.AllowedBiomes is not null && biomeAt is not null && !opt.AllowedBiomes.Contains(biomeAt(gx, gy)))
                return plan.Reject("biome");
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
            sumZ += z;
        }
        if (maxZ - minZ > opt.MaxLocalSlope) return plan.Reject("slope");

        bool rigid = opt.Grounding == StampGrounding.Rigid;
        bool paintIds = opt.PaintLand && v == StampVariant.Original;
        plan.BaseZ = (int)Math.Round(sumZ / (double)plan.Footprint.Count, MidpointRounding.AwayFromZero);

        // ---- land ----
        Dictionary<(int, int), int>? finalZ = null;
        if (stamp.LandCount > 0 && (rigid || paintIds))
        {
            var inFootprint = new HashSet<(int, int)>(plan.Footprint);
            finalZ = new Dictionary<(int, int), int>(stamp.LandCount);
            for (int i = 0; i < stamp.LandCount; i++)
            {
                var (gx, gy) = plan.Footprint[i];
                int terrainZ = terrain.LandZ(gx, gy);
                int z = terrainZ;
                if (rigid)
                {
                    z = plan.BaseZ + stamp.LandZRel[i];
                    if (opt.BlendEdges && IsBorder(inFootprint, gx, gy))
                        z = (int)Math.Round((z + terrainZ) / 2.0, MidpointRounding.AwayFromZero);
                }
                z = Math.Clamp(z, sbyte.MinValue, sbyte.MaxValue);
                finalZ[(gx, gy)] = z;
                ushort id = paintIds ? stamp.LandIds[i] : terrain.LandId(gx, gy);
                if (paintIds && opt.KeepDryLand && TileFlags.IsWaterLandId(id) && !TileFlags.IsWaterLandId(terrain.LandId(gx, gy)))
                    id = terrain.LandId(gx, gy);
                plan.Land.Add(new PlannedLand(gx, gy, id, (sbyte)z));
            }
        }

        // ---- statics ----
        for (int i = 0; i < stamp.StaticCount; i++)
        {
            var (gx, gy) = StaticCell(stamp, i, cx, cy, v);
            if (!terrain.InBounds(gx, gy)) return plan.Reject("static out of bounds");
            if (opt.KeepDryLand && IsWaterStatic(stamp.StaticIds[i]) && !TileFlags.IsWaterLandId(terrain.LandId(gx, gy)))
                continue;

            int anchor = stamp.StaticClusterAnchor.Length > i ? stamp.StaticClusterAnchor[i] : -1;
            int refIdx = anchor >= 0 ? anchor : i;
            int refRx = stamp.StaticRx[refIdx], refRy = stamp.StaticRy[refIdx];
            var refWorld = StaticCell(stamp, refIdx, cx, cy, v);
            if (!terrain.InBounds(refWorld.X, refWorld.Y)) refWorld = (gx, gy);

            int ground = finalZ is not null && finalZ.TryGetValue(refWorld, out var fz) ? fz : terrain.LandZ(refWorld.X, refWorld.Y);
            int rel = stamp.StaticZRel[i];
            int z = stamp.TryGetLandZ(refRx, refRy, out int srcLand)
                ? ground + (rel - srcLand)               // keep the height above its own ground
                : rigid ? plan.BaseZ + rel : ground + rel;
            z = Math.Clamp(z + opt.ZOffset, sbyte.MinValue, sbyte.MaxValue);
            plan.Statics.Add(new PlannedStatic(gx, gy, (sbyte)z, stamp.StaticIds[i], stamp.StaticHues[i]));
        }
        return plan;
    }

    /// <summary>
    /// Plans and applies a stamp to <paramref name="ir"/>. Refuses footprints that overlap
    /// another stamp (hard occupancy); clears scatter statics (soft) under the footprint when
    /// <see cref="StampPlaceOptions.ClearSoftStatics"/>; marks the footprint hard, repainted land
    /// dirty, and records the ground under each static for the resnap pass. Call
    /// <see cref="OccupancyGrid.FlushClears"/> (via <see cref="FinishPass"/>) once per pass.
    /// </summary>
    public static bool TryPlace(GenIR ir, LoadedStamp stamp, int cx, int cy, StampVariant variant,
        StampPlaceOptions opt, out StampPlan plan)
    {
        var biome = ir.Biome;
        Func<int, int, BiomeId>? biomeAt = biome is null ? null : (x, y) => (BiomeId)biome[ir.Index(x, y)];
        plan = Plan(stamp, cx, cy, variant, new GenIrTerrain(ir), opt, biomeAt);
        if (!plan.Ok) return false;

        OccupancyGrid? occ = opt.UseOccupancy ? OccupancyGrid.For(ir) : null;
        if (occ is not null)
        {
            foreach (var (x, y) in plan.Footprint)
                if (occ.IsHard(x, y)) { plan.RejectReason = "overlap"; return false; }
            foreach (var s in plan.Statics)
            {
                if (occ.IsHard(s.X, s.Y)) { plan.RejectReason = "overlap"; return false; }
                if (!opt.ClearSoftStatics && occ.IsSoft(s.X, s.Y)) { plan.RejectReason = "occupied"; return false; }
            }
        }

        // Land
        foreach (var l in plan.Land)
        {
            int idx = ir.Index(l.X, l.Y);
            bool changed = false;
            if (ir.LandId is { } land && land[idx] != l.Id) { land[idx] = l.Id; changed = true; }
            if (ir.Height_Z is { } h && h[idx] != l.Z) { h[idx] = l.Z; changed = true; }
            if (changed) occ?.MarkLandDirty(l.X, l.Y);
        }

        // Occupancy: clear scatter under the footprint, then claim it.
        var footprintIdx = new int[plan.Footprint.Count];
        for (int i = 0; i < plan.Footprint.Count; i++)
        {
            var (x, y) = plan.Footprint[i];
            footprintIdx[i] = ir.Index(x, y);
            if (occ is null) continue;
            if (opt.ClearSoftStatics) occ.QueueClearSoft(x, y);
            occ.MarkHard(x, y);
        }

        var ops = new StaticOp[plan.Statics.Count];
        var terrain = new GenIrTerrain(ir);
        for (int i = 0; i < plan.Statics.Count; i++)
        {
            var s = plan.Statics[i];
            if (occ is not null)
            {
                if (opt.ClearSoftStatics) occ.QueueClearSoft(s.X, s.Y);
                occ.MarkHard(s.X, s.Y);
                occ.RecordGround(s.X, s.Y, terrain.LandZ(s.X, s.Y));
            }
            ops[i] = new StaticOp(StaticOpKind.Add, (ushort)s.X, (ushort)s.Y, s.Z, s.Id, s.Hue);
            ir.StaticOps.Add(ops[i]);
        }
        occ?.AddPlacement(new OccupancyGrid.Placement(opt.Source, stamp.Id, footprintIdx, ops, IsStamp: true));
        return true;
    }

    /// <summary>
    /// End-of-pass bookkeeping for stamp passes: removes the scatter statics queued for
    /// clearing and hands the repainted-land mask to the transition re-blend when available.
    /// Returns the number of statics removed.
    /// </summary>
    public static int FinishPass(GenIR ir, Action<string>? note = null)
    {
        if (ir.Occupancy is not { } occ) return 0;
        int removed = occ.FlushClears();
        StampSeams.Reblend(ir, note);
        return removed;
    }

    /// <summary>World cell of static <paramref name="i"/> under <paramref name="v"/>; cluster members keep their offset to the anchor.</summary>
    public static (int X, int Y) StaticCell(LoadedStamp stamp, int i, int cx, int cy, StampVariant v)
    {
        int rx = stamp.StaticRx[i], ry = stamp.StaticRy[i];
        if (v == StampVariant.Original) return (cx + rx, cy + ry);
        int anchor = stamp.StaticClusterAnchor.Length > i ? stamp.StaticClusterAnchor[i] : -1;
        if (anchor >= 0 && anchor != i)
        {
            int arx = stamp.StaticRx[anchor], ary = stamp.StaticRy[anchor];
            var (tax, tay) = StampTransform.Apply(arx, ary, v);
            return (cx + tax + (rx - arx), cy + tay + (ry - ary));
        }
        var (tx, ty) = StampTransform.Apply(rx, ry, v);
        return (cx + tx, cy + ty);
    }

    private static bool IsBorder(HashSet<(int, int)> fp, int x, int y) =>
        !fp.Contains((x + 1, y)) || !fp.Contains((x - 1, y)) || !fp.Contains((x, y + 1)) || !fp.Contains((x, y - 1));

    /// <summary>Weighted pick (stamp.Weight, non-positive weights count as 0; all-zero falls back to uniform).</summary>
    /// <summary>Water statics: 0x1559 and the whole 0x1796-0x17B2 "water" run (shore foam included).</summary>
    public static bool IsWaterStatic(ushort id) =>
        TileFlags.IsWaterStaticId(id) || (id >= 0x1796 && id <= 0x17B2);

    public static LoadedStamp PickWeighted(IReadOnlyList<LoadedStamp> stamps, Random rng)
    {
        double total = 0;
        foreach (var s in stamps) total += Math.Max(0, s.Weight);
        if (total <= 0) return stamps[rng.Next(stamps.Count)];
        double r = rng.NextDouble() * total;
        foreach (var s in stamps)
        {
            r -= Math.Max(0, s.Weight);
            if (r < 0) return s;
        }
        return stamps[^1];
    }
}
