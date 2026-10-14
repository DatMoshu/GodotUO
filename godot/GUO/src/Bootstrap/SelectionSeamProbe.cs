// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Renderer;

namespace GUO.Host;

/// <summary>Selection forwarding without a shard, install, model or external service.</summary>
public partial class SelectionSeamProbe : Node
{
    private sealed class Victim : GameObject
    {
        internal Victim() : base(null) { }
        internal void DestroyForProbe() => IsDestroyed = true;
        public override bool Draw(UltimaBatcher2D batcher, int x, int y, float depth) => false;
        public override bool TransparentTest(int z) => false;
        public override bool CheckMouseSelection() => false;
    }

    public override void _Ready()
    {
        SplatObject splat = null;
        try
        {
            var victim = new Victim();
            SelectedObject.Object = victim;
            Require(ReferenceEquals(SelectedObject.Object, victim), "ordinary object changed");
            SelectedObject.Object = null;
            Require(SelectedObject.Object == null, "null selection changed");
            splat = new SplatObject(null, new SplatPlacement()) { Victim = victim };
            SelectedObject.Object = splat;
            Require(ReferenceEquals(SelectedObject.Object, victim), "replacement pick did not forward");
            SelectedObject.LastLeftDownObject = SelectedObject.Object;
            Require(ReferenceEquals(SelectedObject.LastLeftDownObject, victim), "drag snapshot lost the victim");
            victim.DestroyForProbe();
            SelectedObject.Object = splat;
            Require(ReferenceEquals(SelectedObject.Object, splat), "destroyed victim returned");
            splat.Victim = null;
            SelectedObject.Object = splat;
            Require(ReferenceEquals(SelectedObject.Object, splat), "unbound splat did not pass through");
            GD.Print("[selection seam] PASS: native/null identity, victim forwarding, drag snapshot, destroyed/unbound fallback");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[selection seam] FAIL: " + ex); GetTree().Quit(1); }
        finally
        {
            SelectedObject.Object = null;
            SelectedObject.LastLeftDownObject = null;
            splat?.Mesh.Dispose();
        }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
