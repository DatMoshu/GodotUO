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

        bool layout = await CheckGumpLayout(host, paperdoll);
        layout &= await CheckFlickReset(host, paperdoll);
        layout &= await CheckShelfChip(host);
        Passed = shelved && pushing && layout;

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

    private static async System.Threading.Tasks.Task<bool> CheckGumpLayout(Node host, PaperDollGump g)
    {
        if (g == null || !DualScreen.ShelfOn) return false;
        int x = g.X, y = g.Y;
        float scale = g.PresentationScale;
        bool locked = g.PresentationLocked, placed = g.PresentationPlaced;
        var main = g.MainPresentationPosition;
        var second = g.SecondPresentationPosition;
        bool enabled = Input.Touch.TouchInput.Enabled;
        float zoom = Client.Game.Scene.Camera.Zoom;
        bool ok = true;
        try
        {
            g.PresentationLocked = false;
            ok &= Input.Touch.GumpPresentation.Transfer(g);
            await InputProbe.Wait(host, 15);
            bool onMain = !Input.Touch.GumpPresentation.OnSecond(g);
            ok &= onMain;
            GD.Print($"[GUO] dual layout: transfer to main, no automatic bounce: {onMain}");
            ok &= Input.Touch.GumpPresentation.Transfer(g);
            await InputProbe.Wait(host, 15);
            bool returned = Input.Touch.GumpPresentation.OnSecond(g) && g.X == x && g.Y == y;
            ok &= returned;
            GD.Print($"[GUO] dual layout: return to saved second-screen position: {returned}");

            // Scaling about a point right of the gump moves its origin left;
            // on the shelf's left edge that must not carry it to the main screen.
            g.X = DualScreen.MainWidth;
            Input.Touch.GumpPresentation.SetScale(g, 1.5f, new Compat.Point(g.X + g.Width, g.Y + 40));
            bool staysOnShelf = Input.Touch.GumpPresentation.OnSecond(g);
            ok &= staysOnShelf;
            GD.Print($"[GUO] dual layout: scaling at the shelf's left edge stays on the shelf: {staysOnShelf}");
            Input.Touch.GumpPresentation.Reset(g);
            g.X = x; g.Y = y;

            Input.Touch.GumpPresentation.SetScale(g, 1.25f, new Compat.Point(g.X, g.Y));
            var local = new Compat.Point(g.X + 60, g.Y + 90);
            var at = Input.Touch.GumpPresentation.ToScreen(g, local);
            var back = Input.Touch.GumpPresentation.ToLocal(g, at);
            bool inverse = System.Math.Abs(local.X - back.X) <= 1 && System.Math.Abs(local.Y - back.Y) <= 1;
            ok &= inverse;
            GD.Print($"[GUO] dual layout: shelf transform round trip: {inverse}");

            int shelfX = DualScreen.MainWidth;
            Input.Touch.GumpPresentation.MoveDragged(g,
                new Compat.Point(shelfX + 30, 100), new Compat.Point(shelfX - 10, 100));
            bool horizontal = Input.Touch.GumpPresentation.OnSecond(g);
            Input.Touch.GumpPresentation.MoveDragged(g,
                new Compat.Point(shelfX + 30, 20), new Compat.Point(shelfX + 30, 0));
            bool up = !Input.Touch.GumpPresentation.OnSecond(g);
            await InputProbe.Wait(host, 15);
            up &= !Input.Touch.GumpPresentation.OnSecond(g);
            int bottom = Input.Touch.GumpPresentation.DisplayBounds(false).Height;
            Input.Touch.GumpPresentation.MoveDragged(g,
                new Compat.Point(30, bottom - 20), new Compat.Point(30, bottom - 1));
            bool down = Input.Touch.GumpPresentation.OnSecond(g);
            ok &= horizontal && up && down;
            GD.Print($"[GUO] dual layout: horizontal stays, drag up, drag down: {horizontal}, {up}, {down}");
            at = Input.Touch.GumpPresentation.ToScreen(g, new Compat.Point(g.X + 60, g.Y + 90));

            // Contacts on different panels may coexist, but must never form a pinch.
            Input.Touch.TouchInput.Enabled = true;
            Input.Touch.TouchInput.CancelGesture();
            float before = g.PresentationScale;
            Vector2 first = new Vector2(400, 400) * Client.Game.DpiScale;
            Vector2 last = new Vector2(at.X, at.Y) * Client.Game.DpiScale;
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 0, Position = first, Pressed = true });
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 32, Position = last, Pressed = true });
            Input.Touch.TouchInput.Handle(new InputEventScreenDrag { Index = 32, Position = last + new Vector2(100, 0) });
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 32, Position = last, Pressed = false });
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 0, Position = first, Pressed = false });
            bool separate = before == g.PresentationScale && zoom == Client.Game.Scene.Camera.Zoom;
            ok &= separate;
            GD.Print($"[GUO] dual layout: cross-display contacts do not pinch: {separate}");
        }
        finally
        {
            Input.Touch.TouchInput.CancelGesture();
            Input.Touch.TouchInput.Enabled = enabled;
            g.X = x; g.Y = y; g.PresentationScale = scale;
            g.PresentationLocked = locked; g.PresentationPlaced = placed;
            g.MainPresentationPosition = main; g.SecondPresentationPosition = second;
        }
        return ok;
    }

    private static uint _heldSerial;

    /// <summary>
    /// Pick up one item from the backpack, which the shelf keeps on the
    /// second screen, and put the pointer there: the held-item marker should
    /// then draw at the pointer on the second screen and as a badge on the
    /// main one. False when there is nothing to pick up.
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> HoldOnShelf(Node host)
    {
        Game.World world = Client.Game.UO.World;
        Game.GameObjects.Item backpack = world.Player?.FindItemByLayer(Game.Data.Layer.Backpack);
        Game.GameObjects.Item item = null;

        for (var i = (Game.GameObjects.Item)backpack?.Items; i != null; i = (Game.GameObjects.Item)i.Next)
        {
            if (!i.IsDestroyed && !i.IsMulti && i.Amount >= 1)
            {
                item = i;

                break;
            }
        }

        if (item == null)
        {
            GD.PrintErr("[GUO] dual screen: held FAIL nothing in the backpack to pick up");

            return false;
        }

        // An empty stretch of the second screen, so the photograph shows the
        // marker and nothing else there.
        var at = new Compat.Point(DualScreen.MainWidth + 600, 600);

        Input.Mouse.Position = at;

        if (!Game.GameActions.PickUp(world, item.Serial, 0, 0, item.Amount))
        {
            GD.PrintErr($"[GUO] dual screen: held FAIL could not pick up 0x{item.Graphic:X4}");

            return false;
        }

        _heldSerial = item.Serial;

        for (int i = 0; i < 20; i++)
        {
            Input.Mouse.Position = at;
            await InputProbe.Wait(host, 1);
        }

        Game.ItemHold hold = Client.Game.UO.GameCursor.ItemHold;

        GD.Print(
            $"[GUO] dual screen: held 0x{hold.Graphic:X4} x{hold.Amount} enabled={hold.Enabled} "
            + $"pointer {at.X},{at.Y} (main window {DualScreen.MainWidth} wide)"
        );

        return hold.Enabled;
    }

    /// <summary>Put the item <see cref="HoldOnShelf"/> took back into the backpack.</summary>
    public static async System.Threading.Tasks.Task DropBack(Node host)
    {
        Game.World world = Client.Game.UO.World;
        Game.GameObjects.Item backpack = world.Player?.FindItemByLayer(Game.Data.Layer.Backpack);

        if (_heldSerial != 0 && backpack != null && Client.Game.UO.GameCursor.ItemHold.Enabled)
        {
            Game.GameActions.DropItem(_heldSerial, 0xFFFF, 0xFFFF, 0, backpack.Serial);
            await InputProbe.Wait(host, 30);
            GD.Print($"[GUO] dual screen: held item dropped back, holding={Client.Game.UO.GameCursor.ItemHold.Enabled}");
        }
    }

    /// <summary>
    /// Hold-and-flick's Reset (GumpFlick): a gump sent away and back stays on
    /// its home screen; one left on the other screen goes home.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> CheckFlickReset(Node host, PaperDollGump g)
    {
        if (g == null || !DualScreen.ShelfOn) return false;

        bool home = Input.Touch.GumpPresentation.OnSecond(g);
        Input.Touch.GumpPresentation.Transfer(g);
        Input.Touch.GumpPresentation.Transfer(g);
        await InputProbe.Wait(host, 5);
        Input.Touch.GumpFlick.Perform(g, Input.Touch.FlickAction.Reset);
        await InputProbe.Wait(host, 5);
        bool stays = Input.Touch.GumpPresentation.OnSecond(g) == home;

        Input.Touch.GumpPresentation.Transfer(g);
        await InputProbe.Wait(host, 5);
        Input.Touch.GumpFlick.Perform(g, Input.Touch.FlickAction.Reset);
        await InputProbe.Wait(host, 5);
        bool back = Input.Touch.GumpPresentation.OnSecond(g) == home;

        GD.Print($"[GUO] dual layout: flick Reset after up-and-back stays home, from away goes home: {stays}, {back} (home is the {(home ? "second" : "main")} screen)");

        return stays && back;
    }

    /// <summary>
    /// C8 on the Thor: a window on the lower screen minimised to a chip, the
    /// chip on the lower screen (not on the main screen's bar), and a tap on
    /// it bringing the window back. Needs the touch layer (--touch).
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> CheckShelfChip(Node host)
    {
        Input.Touch.TouchGumpBar bar = Input.Touch.TouchInput.Bar;
        Gump g = null;

        foreach (Gump candidate in Game.Managers.UIManager.Gumps)
        {
            if (!candidate.IsDisposed && candidate.IsVisible && Input.Touch.GumpPresentation.Supports(candidate)
                && Input.Touch.GumpPresentation.OnSecond(candidate))
            {
                g = candidate;
                break;
            }
        }

        if (bar == null || g == null)
        {
            GD.Print($"[GUO] dual layout: shelf chip skipped (bar {bar != null}, a shelved gump {g != null}); run with --touch");
            return bar == null;
        }

        int x = g.X, y = g.Y;
        Input.Touch.GumpFlick.Perform(g, Input.Touch.FlickAction.Minimise);
        await InputProbe.Wait(host, 5);

        Rect2? chip = bar.ChipRect(g);
        float lowerLeft = DualScreen.MainWidth * Client.Game.DpiScale;
        bool onLower = chip is Rect2 r && r.Position.X >= lowerLeft;

        await InputProbe.Wait(host, 10);

        if (chip is Rect2 c)
        {
            Vector2 at = c.GetCenter();
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 0, Position = at, Pressed = true });
            await InputProbe.Wait(host, 3);
            Input.Touch.TouchInput.Handle(new InputEventScreenTouch { Index = 0, Position = at, Pressed = false });
            await InputProbe.Wait(host, 10);
        }

        bool restored = g.IsVisible && g.X == x && g.Y == y && bar.ChipRect(g) == null;
        GD.Print($"[GUO] dual layout: a lower-screen window minimises to a chip on the lower screen and a tap restores it: {onLower}, {restored} (chip {chip})");

        return onLower && restored;
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
