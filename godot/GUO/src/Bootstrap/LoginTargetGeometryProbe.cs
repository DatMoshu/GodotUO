// SPDX-License-Identifier: BSD-2-Clause
// Disconnected layout qualification only. No client boot, art, account or network.
using System;
using GUO.Pregame3D;
using Godot;

namespace GUO.Host;

public partial class LoginTargetGeometryProbe : Node
{
    private int _checks;

    public override void _Ready()
    {
        try
        {
            GetTree().Root.Size = new Vector2I(1280, 720);
            Rect2 viewport = new(0, 0, 1280, 720);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float drift in new[] { -3f, 0f, 3f })
            {
                // Actual transformed bounds, not an extra PointerScale conversion.
                Rect2 rect = new(new Vector2(160 + drift, 40) + new Vector2(300, 200) * scale,
                    new Vector2(80, 20) * scale);
                Need(InputProbe.LoginTargetPoint(rect, viewport, out Vector2 at), "transformed rect admitted");
                Need(at == rect.GetCenter(), "drift/overlay scale uses current global center");
                foreach (float dpi in new[] { 1f, 2f, 4f })
                {
                    InputProbe.PointerScale = dpi;
                    Need(InputProbe.LoginTargetPoint(rect, viewport, out Vector2 next) && next == at,
                        "global geometry independent of Classic DPI multiplier");
                }
            }
            Need(!InputProbe.LoginTargetPoint(new Rect2(0, 0, 0, 20), viewport, out _), "zero size rejected");
            Need(!InputProbe.LoginTargetPoint(new Rect2(0, 0, -1, 20), viewport, out _), "negative size rejected");
            Need(!InputProbe.LoginTargetPoint(new Rect2(float.NaN, 0, 20, 20), viewport, out _), "nonfinite rejected");
            Need(!InputProbe.LoginTargetPoint(new Rect2(2000, 0, 20, 20), viewport, out _), "outside rejected");

            var parent = new Control { Position = new Vector2(160, 0), Scale = new Vector2(1.5f, 1.5f),
                Size = new Vector2(400, 400), ClipContents = true };
            var target = new Control { Position = new Vector2(20, 20), Size = new Vector2(80, 20) };
            AddChild(parent);
            parent.AddChild(target);
            Need(InputProbe.LoginTargetControlReady(target, out Vector2 original),
                $"actual transformed Control ready: rect={target.GetGlobalRect()} viewport={target.GetViewport().GetVisibleRect()} tree={target.IsInsideTree()} visible={target.IsVisibleInTree()} clip={parent.GetGlobalRect()}");
            target.Position += new Vector2(3, 2);
            Need(InputProbe.LoginTargetControlReady(target, out Vector2 moved) && moved != original, "live bounds refresh after drift");
            target.Hide();
            Need(!InputProbe.LoginTargetControlReady(target, out _), "hidden rejected");
            target.Show();
            target.Position = new Vector2(500, 20);
            Need(!InputProbe.LoginTargetControlReady(target, out _), "ancestor clipping rejected");
            target.Position = new Vector2(20, 20);
            target.QueueFree();
            Need(!InputProbe.LoginTargetControlReady(target, out _), "queued target rejected");
            var freed = new Control();
            freed.Free();
            Need(!InputProbe.LoginTargetControlReady(freed, out _), "freed target rejected");
            Need(!InputProbe.LoginTargetControlReady(null, out _), "absent target rejected");
            var row = new UiFocus(target) { Tag = "char:Existing" };
            var other = new UiFocus(target) { Tag = "char:Other" };
            Need(ReferenceEquals(InputProbe.ExpectedCharacterRow(new[] { other,row }, "Existing"), row), "exact named actual row");
            Need(InputProbe.ExpectedCharacterRow(new[] { row }, "existing") == null, "wrong case rejects");
            Need(InputProbe.ExpectedCharacterRow(new[] { row }, "Absent") == null, "missing rejects");
            Need(InputProbe.ExpectedCharacterRow(new[] { row }, "") == null, "empty expectation rejects");
            Need(InputProbe.ExpectedCharacterRow(null, "Existing") == null, "absent list rejects");
            Need(InputProbe.ExpectedCharacterRow(new[] { row,row }, "Existing") == null, "ambiguous duplicate rejects");
            Need(PerfProbe.CurrentLocationValid(true,true,true,0,0), "existing world map admitted");
            Need(!PerfProbe.CurrentLocationValid(false,true,true,0,0), "login-only rejected");
            Need(!PerfProbe.CurrentLocationValid(true,false,true,0,0), "no player rejected");
            Need(!PerfProbe.CurrentLocationValid(true,true,true,1,0), "wrong map rejected");
            Need(!PerfProbe.CurrentLocationValid(true,true,false,0,0), "non-world scene rejected");
            string[] built = { "Existing" };
            Need(CharacterStage.ProbeListCurrent(built,built), "built list identity current");
            Need(!CharacterStage.ProbeListCurrent(built,new[] { "Existing" }), "same-text replaced list rejects stale rows");
            Need(!CharacterStage.ProbeListCurrent(null,null), "unbuilt list rejected");
            Need(!CharacterStage.ProbeListCurrent(built,null), "removed list rejected");
            parent.Free();
            GD.Print("[GUO] login target geometry PASS " + _checks);
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PushError("login target geometry FAIL " + ex.Message);
            GetTree().Quit(1);
        }
        finally { InputProbe.PointerScale = 1; }
    }

    private void Need(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(name);
    }
}
