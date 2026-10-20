// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class WaterWorkbenchProbe
{
    private void CheckReplayControls(WaterPanel panel)
    {
        byte[] style = WaterAuthoring.Serialize(panel.Style);
        string path = panel.DraftPath; int history = panel.HistoryPosition;
        bool freeze = panel.FreezeToggle.ButtonPressed;
        var hull = Descendants(panel).OfType<CheckBox>().Single(c => c.Text == "Moving hull");
        bool moving = hull.ButtonPressed;
        try
        {
            hull.ButtonPressed = true;
            panel.ReplayTest.Select(2); panel.ReplayTime.Value = 1.5;
            Press(panel, "Replay at time");
            Require(panel.FreezeToggle.ButtonPressed && panel.Preview.Paused && Math.Abs(panel.Preview.Seconds - 1.5f) < .001f
                && panel.Preview.Boat && hull.ButtonPressed,
                "replay pauses at the selected time and preserves the free-running hull toggle");
            Require(panel.HistoryPosition == history && panel.DraftPath == path
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(style),
                "replay does not edit preset settings, document identity or history");
            Press(panel, "Step 0.05 s");
            Require(Math.Abs(panel.Preview.Seconds - 1.55f) < .001f && panel.Preview.Paused,
                "step rebuilds the next fixed-time frozen pose");
            panel.ReplayTime.Value = 5; panel.ReplayPreview();
            Require(panel.Preview.Seconds == 5 && Descendants(panel).OfType<Button>().Single(b => b.Text == "Step 0.05 s").Disabled,
                "repeatable test duration is bounded to five seconds");
            panel.WorldToggle.ButtonPressed = true;
            panel.ReplayPreview();
            Require(panel.Preview.Seconds == 5 && panel.StatusText.StartsWith("Turn off Preview in World")
                && Descendants(panel).OfType<Button>().Single(b => b.Text == "Replay at time").Disabled,
                "World mode refuses synthetic replay without resetting its preview clock");
        }
        finally
        {
            panel.WorldToggle.ButtonPressed = false;
            hull.ButtonPressed = moving;
            panel.FreezeToggle.ButtonPressed = freeze;
            panel.ReplayTime.Value = 1.5; panel.ReplayTest.Select(0);
        }
    }

    private async Task CheckReplayRendering(WaterPanel panel, WaterPreview large, SubViewport viewport, string stills, string renderer)
    {
        var reference = WaterAuthoring.Presets[1];
        large.SetStyle(reference with { Wave = .75f, Tint = new[] { .5f, .08f, .15f } });
        large.SetReference(reference); large.Compare = true; large.Classic = false; large.SetScene(0);
        large.Replay(2, 1.5f);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var first = large.Capture();
        large.Paused = false; large.Boat = true; large.Splash(); large.Advance(.137f);
        large.Replay(2, 1.5f);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var repeated = large.Capture();
        Require(first.GetData().AsSpan().SequenceEqual(repeated.GetData()),
            "replay restores byte-identical reference and draft pixels after unrelated inputs and clock changes");
        large.Replay(2, 2);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var later = large.Capture();
        Require(DifferentPixels(first, later, 0, later.GetWidth()) > 100,
            "changing replay time changes the rendered test pose");
        large.Replay(0, 1.5f);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var splashOnly = large.Capture();
        Require(DifferentPixels(first, splashOnly, 0, first.GetWidth()) > 20,
            "splash-only replay differs from the combined splash and hull test");
        large.Classic = true; large.Replay(0, 1);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var classic = large.Capture();
        large.Replay(2, 3);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var classicLater = large.Capture();
        Require(classic.GetData().AsSpan().SequenceEqual(classicLater.GetData()),
            "Classic bypass remains pixel-identical across different repeatable inputs and times");
        panel.LoadStyle(reference); panel.KeepReference();
        panel.Change(reference with { Wave = .75f, Tint = new[] { .5f, .08f, .15f } });
        panel.ReplayTest.Select(2); panel.ReplayTime.Value = 1.5; panel.ReplayPreview();
        panel.ShowReplayControls();
        large.Classic = false; large.Replay(2, 1.5f);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var shot = viewport.GetTexture().GetImage();
        Require(shot.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_replay-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
            "capture repeatable test controls with the frozen comparison");
        panel.ShowReplayControls(false); panel.ClearReference(); panel.ScrollVertical = 0;
        large.SetReference(null); large.Classic = false; large.Boat = false;
    }
}
#endif
