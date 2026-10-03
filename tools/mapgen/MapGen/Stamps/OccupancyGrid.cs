using CentrED.MapGen.IR;

namespace CentrED.MapGen.Stamps;

/// <summary>
/// Shared per-tile bookkeeping for every pass that places statics or stamps. Lives on
/// <see cref="GenIR.Occupancy"/> (allocated on first use by <see cref="For"/>).
///
/// <list type="bullet">
/// <item><b>Soft</b>: a scatter object (tree, bush, rock group) sits here. Scatter passes
/// skip soft and hard cells; a stamp may clear soft statics inside its footprint.</item>
/// <item><b>Hard</b>: inside a placed stamp's footprint, or a POI marker. Nothing else may
/// be placed here.</item>
/// <item><b>LandDirty</b>: a stamp repainted the land id/Z here after the transition pass;
/// <see cref="StampSeams"/> hands this mask to the transition re-blend.</item>
/// <item><b>Ground</b>: <see cref="GroundZ"/> holds the land Z the statics on this tile were
/// placed against, so <c>StaticResnapPass</c> can move them when a later pass changes
/// the land (validator ZStep / FlatWater fixes).</item>
/// </list>
/// </summary>
public sealed class OccupancyGrid
{
    [Flags]
    public enum Cell : byte
    {
        Free = 0,
        Soft = 1,
        Hard = 2,
        LandDirty = 4,
        Ground = 8,
    }

    /// <summary>One placed stamp or multi-tile scatter group, kept for the validation checks.</summary>
    public sealed record Placement(string Source, string Id, int[] Footprint, StaticOp[] Ops, bool IsStamp);

    private readonly GenIR _ir;
    private readonly byte[] _cells;
    private sbyte[]? _groundZ;
    // cell -> number of StaticOps that existed when the clear was queued; ops before that
    // index on the cell are removed by FlushClears.
    private readonly Dictionary<int, int> _pendingClears = new();
    private readonly List<Placement> _placements = new();

    public OccupancyGrid(GenIR ir)
    {
        _ir = ir;
        _cells = new byte[ir.TileCount];
    }

    /// <summary>The grid on <paramref name="ir"/>, allocating it on first use.</summary>
    public static OccupancyGrid For(GenIR ir) => ir.Occupancy ??= new OccupancyGrid(ir);

    public int Width => _ir.Width;
    public int Height => _ir.Height;
    public IReadOnlyList<Placement> Placements => _placements;
    public int PendingClearCount => _pendingClears.Count;

    public Cell Get(int x, int y) => (Cell)_cells[_ir.Index(x, y)];
    public Cell GetAt(int index) => (Cell)_cells[index];

    /// <summary>True when neither a scatter object nor a stamp occupies (x, y).</summary>
    public bool IsFree(int x, int y) => (_cells[_ir.Index(x, y)] & (byte)(Cell.Soft | Cell.Hard)) == 0;
    public bool IsHard(int x, int y) => (_cells[_ir.Index(x, y)] & (byte)Cell.Hard) != 0;
    public bool IsSoft(int x, int y) => (_cells[_ir.Index(x, y)] & (byte)Cell.Soft) != 0;

    public void MarkSoft(int x, int y) => _cells[_ir.Index(x, y)] |= (byte)Cell.Soft;
    public void MarkHard(int x, int y) => _cells[_ir.Index(x, y)] |= (byte)Cell.Hard;
    public void MarkLandDirty(int x, int y) => _cells[_ir.Index(x, y)] |= (byte)Cell.LandDirty;

    /// <summary>Remembers the land Z that statics on (x, y) were placed against.</summary>
    public void RecordGround(int x, int y, int landZ)
    {
        int i = _ir.Index(x, y);
        _groundZ ??= new sbyte[_ir.TileCount];
        _groundZ[i] = (sbyte)Math.Clamp(landZ, sbyte.MinValue, sbyte.MaxValue);
        _cells[i] |= (byte)Cell.Ground;
    }

    public bool TryGetGround(int index, out sbyte z)
    {
        if (_groundZ is not null && (_cells[index] & (byte)Cell.Ground) != 0) { z = _groundZ[index]; return true; }
        z = 0;
        return false;
    }

    /// <summary>Updates every recorded ground Z to the current land (after a resnap).</summary>
    public void RebaseGround(sbyte[] heightZ)
    {
        if (_groundZ is null) return;
        for (int i = 0; i < _cells.Length; i++)
            if ((_cells[i] & (byte)Cell.Ground) != 0) _groundZ[i] = heightZ[i];
    }

    /// <summary>
    /// Queues removal of the scatter statics already on (x, y) (soft cells only). Statics
    /// added after this call — the stamp's own — are kept. Applied by <see cref="FlushClears"/>.
    /// </summary>
    public void QueueClearSoft(int x, int y)
    {
        int i = _ir.Index(x, y);
        if ((_cells[i] & (byte)Cell.Soft) == 0) return;
        if (!_pendingClears.ContainsKey(i)) _pendingClears[i] = _ir.StaticOps.Count;
        _cells[i] = (byte)(_cells[i] & ~(byte)Cell.Soft);
    }

    /// <summary>Removes the queued scatter statics in one sweep. Returns the number removed.</summary>
    public int FlushClears()
    {
        if (_pendingClears.Count == 0) return 0;
        var ops = _ir.StaticOps;
        int write = 0, removed = 0;
        for (int read = 0; read < ops.Count; read++)
        {
            var op = ops[read];
            if (op.Kind == StaticOpKind.Add
                && _pendingClears.TryGetValue(_ir.Index(op.X, op.Y), out int limit)
                && read < limit)
            {
                removed++;
                continue;
            }
            ops[write++] = op;
        }
        ops.RemoveRange(write, ops.Count - write);
        _pendingClears.Clear();
        return removed;
    }

    public void AddPlacement(Placement p) => _placements.Add(p);

    /// <summary>Boolean mask (TileCount long) of tiles whose land a stamp repainted.</summary>
    public bool[] LandDirtyMask(out int count)
    {
        var mask = new bool[_cells.Length];
        count = 0;
        for (int i = 0; i < _cells.Length; i++)
            if ((_cells[i] & (byte)Cell.LandDirty) != 0) { mask[i] = true; count++; }
        return mask;
    }

    /// <summary>Clears the LandDirty bit everywhere (after the seams were re-blended).</summary>
    public void ClearLandDirty()
    {
        for (int i = 0; i < _cells.Length; i++) _cells[i] = (byte)(_cells[i] & ~(byte)Cell.LandDirty);
    }
}
