// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Configuration;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    // PORT DEVIATION (GUO): frame-invariant inputs for the synchronous gather.
    // Never retained on objects or shared with Draw/highlight restoration.
    private readonly struct GatherInputs
    {
        internal readonly int ViewRange, TransparencyZ;
        internal readonly bool NoRangeColor, DeadTint, UseCot, MouseInsideCot;

        internal GatherInputs(int viewRange, int transparencyZ, bool noRangeColor,
            bool deadTint, bool useCot, bool mouseInsideCot)
        {
            ViewRange = viewRange; TransparencyZ = transparencyZ;
            NoRangeColor = noRangeColor; DeadTint = deadTint;
            UseCot = useCot; MouseInsideCot = mouseInsideCot;
        }

        internal bool Same(in GatherInputs other) => ViewRange == other.ViewRange
            && TransparencyZ == other.TransparencyZ && NoRangeColor == other.NoRangeColor
            && DeadTint == other.DeadTint && UseCot == other.UseCot && MouseInsideCot == other.MouseInsideCot;

        // ChunkReuse: the walk reads TransparencyZ and MouseInsideCot only
        // under a circle of transparency, so without one a step up or down
        // (TransparencyZ is the player's Z + 5) does not change its outcome.
        internal bool SameForReuse(in GatherInputs other) => UseCot || other.UseCot
            ? Same(other)
            : ViewRange == other.ViewRange && NoRangeColor == other.NoRangeColor && DeadTint == other.DeadTint;
    }

    private GatherInputs CaptureGatherInputs()
    {
        var profile = ProfileManager.CurrentProfile;
        return new GatherInputs(_world.ClientViewRange, _world.Player.Z + 5,
            profile.NoColorObjectsOutOfRange, _world.Player.IsDead && profile.EnableBlackWhiteEffect,
            profile.UseCircleOfTransparency, IsMouseInsideCotCircle());
    }

    // Opt-in runtime assertion: prove the captured inputs still equal live
    // inputs at each meshed object's decision. No diagnostic allocations.
    private static readonly bool VerifyGatherInputs = Environment.GetEnvironmentVariable("GUO_VERIFY_GATHER_INPUTS") == "1";
    internal static long GatherInputChecks, GatherInputMismatches;

    private void CheckGatherInputs(in GatherInputs inputs)
    {
        if (!VerifyGatherInputs) return;
        GatherInputChecks++;
        if (!inputs.Same(CaptureGatherInputs()))
        {
            GatherInputMismatches++;
            throw new InvalidOperationException("GUO gather inputs changed during synchronous preparation.");
        }
    }
}
