// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// Log in, let the second screen come up, and say on the log what it did
/// and what it cost. The Android tooling reads the lines back through
/// logcat and photographs both panels; on a desktop with
/// <c>--dual-screen WxH</c> the same lines come out of the simulator.
/// </summary>
/// <remarks>
/// Every line starts with <c>[GUO] dual screen:</c>. The verdict is the last
/// one, <c>ok</c> or <c>FAIL</c>, and the frame rates on the line before it
/// are the cost measurement ADR-0009 quotes: the main screen with the
/// second one running, then with it suspended, over the same scene.
/// </remarks>
internal static class DualProbe
{
    public static bool Passed { get; private set; }

    /// <summary>Frames to average a frame rate over. Three seconds at 60.</summary>
    private const int MeasureFrames = 180;

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        // The login goes through the mouse path in client pixels, which the
        // input probe already proves; the touch layer steps aside for it, and
        // on a device the client's scale is put back on the probe's aim.
        // The controller is added deferred and picks its scale a frame later.
        await InputProbe.Wait(host, 120);

        bool touch = Input.Touch.TouchInput.Enabled;
        Input.Touch.TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;

        await InputProbe.EnterTheWorld(host, 0);

        InputProbe.PointerScale = 1f;
        Input.Touch.TouchInput.Enabled = touch;

        Game.World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] dual screen: FAIL never got into the world -- is the dev shard running?");

            return;
        }

        if (!DualScreen.HasSecondaryDisplay)
        {
            // Not a failure of the feature: there is nothing to use. The
            // doctor says so before the run; the probe says so after it.
            GD.Print("[GUO] dual screen: no second display on this device; nothing to probe");
            Passed = true;

            return;
        }

        // The gumps a player keeps open. Opening them is what the shelf reacts
        // to; upstream's own actions, so the gumps arrive as they always do.
        Game.GameActions.OpenPaperdoll(world, world.Player);
        Game.GameActions.OpenBackpack(world);
        Game.GameActions.OpenStatusBar(world);
        Game.GameActions.OpenJournal(world);

        // Activation happens in DualScreen's own _Process once the profile
        // and the world agree; the presentation comes up on Android's UI
        // thread after that. Give both a moment.
        for (int i = 0; i < 300 && !DualScreen.Active; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        if (!DualScreen.Active)
        {
            GD.PrintErr(
                $"[GUO] dual screen: FAIL not active after 300 frames "
                + $"(forced off {DualScreen.ForcedOff}, profile {Configuration.ProfileManager.CurrentProfile?.DualScreenEnabled}, "
                + $"last error \"{DualScreen.LastError}\")"
            );

            return;
        }

        await InputProbe.Wait(host, 120);

        int presentedBefore = DualScreen.FramesPresented;

        double fpsOn = await MeasureFps(host);

        DualScreen.Suspended = true;
        await InputProbe.Wait(host, 30);
        double fpsOff = await MeasureFps(host);
        DualScreen.Suspended = false;

        await InputProbe.Wait(host, 30);

        int presented = DualScreen.FramesPresented - presentedBefore;

        PaperDollGump paperdoll = Game.Managers.UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        StatusGumpBase status = StatusGumpBase.GetStatusGump();

        GD.Print(
            $"[GUO] dual screen: shelf {DualScreen.ShelfCount} gump(s) beyond x={DualScreen.MainWidth}; "
            + $"paperdoll at {paperdoll?.X},{paperdoll?.Y} status at {status?.X},{status?.Y}; "
            + $"world {Client.Game.Scene.Camera.Bounds.Width}x{Client.Game.Scene.Camera.Bounds.Height} "
            + $"in a {Client.Game.ClientBounds.Width}x{Client.Game.ClientBounds.Height} window"
        );

        var gumps = new System.Text.StringBuilder();

        foreach (Gump g in Game.Managers.UIManager.Gumps)
        {
            if (!g.IsDisposed && g.Width > 0 && g.Height > 0)
            {
                gumps.Append($" {g.GetType().Name}@{g.X},{g.Y}:{g.Width}x{g.Height}");
            }
        }

        GD.Print($"[GUO] dual screen: gumps{gumps}");

        GD.Print(
            $"[GUO] dual screen: presented {presented} frames during the on-measurement, "
            + $"last push {DualScreen.LastPresentMs:F2} ms, touches taken {DualScreen.TouchesTaken}"
        );

        GD.Print($"[GUO] dual screen: fps on={fpsOn:F1} off={fpsOff:F1}");

        bool shelved = DualScreen.ShelfCount >= 3;
        bool pushing = presented > 0 || DualScreen.LastError.Length == 0;

        Passed = shelved && pushing;

        if (Passed)
        {
            GD.Print("[GUO] dual screen: ok");
        }
        else
        {
            GD.PrintErr(
                $"[GUO] dual screen: FAIL shelved={shelved} pushing={pushing} last error \"{DualScreen.LastError}\""
            );
        }
    }

    /// <summary>Frames per second over a fixed number of frames, from the clock.</summary>
    private static async System.Threading.Tasks.Task<double> MeasureFps(Node host)
    {
        ulong started = Godot.Time.GetTicksUsec();

        await InputProbe.Wait(host, MeasureFrames);

        double seconds = (Godot.Time.GetTicksUsec() - started) / 1_000_000.0;

        return seconds > 0 ? MeasureFrames / seconds : 0;
    }
}
