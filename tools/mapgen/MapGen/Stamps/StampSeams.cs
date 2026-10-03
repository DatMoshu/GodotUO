using System.Reflection;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;

namespace CentrED.MapGen.Stamps;

/// <summary>
/// Stamps repaint land after the Land Transitions pass has run, so the seam between a
/// stamp's own land and the generated terrain has no transition tiles. Placers mark the
/// repainted tiles in <see cref="OccupancyGrid"/> (LandDirty); this hook hands that mask
/// to the transition pass so it can re-blend just those tiles and their neighbours.
///
/// <para>Contract expected on the terrain side (looked up by reflection so this file
/// builds before it exists):
/// <c>public static … LandTransitionPass.RunOnMask(GenIR ir, bool[] mask, …optional…)</c>,
/// where <c>mask</c> is TileCount long, true on every tile a stamp repainted. The pass is
/// expected to recompute transitions on the masked tiles plus a one-tile ring around them
/// (the seam), leaving everything else untouched.</para>
/// </summary>
public static class StampSeams
{
    private static readonly Lazy<MethodInfo?> RunOnMask = new(() =>
        typeof(LandTransitionPass)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (m.Name != "RunOnMask") return false;
                var ps = m.GetParameters();
                return ps.Length >= 2
                    && ps[0].ParameterType == typeof(GenIR)
                    && ps[1].ParameterType == typeof(bool[])
                    && ps.Skip(2).All(p => p.IsOptional);
            }));

    /// <summary>True when the terrain side exposes <c>LandTransitionPass.RunOnMask</c>.</summary>
    public static bool IsAvailable => RunOnMask.Value is not null;

    /// <summary>
    /// Re-blends the transitions on tiles stamps repainted since the last call, if the
    /// transition pass supports it. Returns the number of masked tiles handed over (0 when
    /// nothing was dirty or the hook is unavailable — then the mask is kept).
    /// </summary>
    public static int Reblend(GenIR ir, Action<string>? note = null)
    {
        if (ir.Occupancy is not { } occ) return 0;
        var mask = occ.LandDirtyMask(out int count);
        if (count == 0) return 0;
        var m = RunOnMask.Value;
        if (m is null)
        {
            note?.Invoke($"{count} stamp-painted tiles not re-blended (LandTransitionPass.RunOnMask not available)");
            return 0;
        }
        var args = new object?[m.GetParameters().Length];
        args[0] = ir;
        args[1] = mask;
        for (int i = 2; i < args.Length; i++) args[i] = Type.Missing;
        m.Invoke(null, BindingFlags.OptionalParamBinding, null, args, null);
        occ.ClearLandDirty();
        note?.Invoke($"re-blended transitions on {count} stamp-painted tiles");
        return count;
    }
}
