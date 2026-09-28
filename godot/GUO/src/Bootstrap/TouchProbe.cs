// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Game.UI.Gumps.Login;
using GUO.Input;
using GUO.Input.Touch;

namespace GUO.Host;

/// <summary>
/// Drive the touch layer with synthetic fingers and check the client did
/// what a mouse would have made it do.
/// </summary>
/// <remarks>
/// Fingers go in as <see cref="InputEventScreenTouch"/> and
/// <see cref="InputEventScreenDrag"/> through <c>Input.ParseInputEvent</c>,
/// the same queue a touch screen feeds, so the whole path from the window
/// to the client is exercised. What comes out is judged two ways: the
/// layer's own trace says what it translated a gesture into, and the client
/// says whether it reacted -- a text field took focus, the character moved,
/// a gump opened, the camera zoomed.
///
/// The login screen part needs nothing but the client data. The rest needs
/// the dev shard, as every probe that plays does; without it those checks
/// fail and say so.
/// </remarks>
internal static class TouchProbe
{
    public static bool Passed { get; private set; }

    private static readonly List<(string What, bool Ok)> Checks = new();

    /// <summary>The account field of the login gump, in client pixels (see InputProbe).</summary>
    private static readonly Vector2 AccountField = new(320, 297);

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        Checks.Clear();

        await Frames(host, 200);

        Check("the touch layer is on", TouchInput.Enabled);
        Check("the login gump is up", UIManager.GetGump<LoginGump>() != null);

        // --- login screen --------------------------------------------------

        TouchInput.Trace.Clear();
        await Tap(host, Client(AccountField + new Vector2(InputProbe.LoginOrigin().X, InputProbe.LoginOrigin().Y)));
        await Frames(host, 10);

        Check(
            "a tap became a left click",
            TouchInput.Trace.Exists(t => t.StartsWith("tap -> left click")),
            string.Join(" | ", TouchInput.Trace)
        );
        Check(
            "the tapped field took keyboard focus",
            UIManager.KeyboardFocusControl != null,
            UIManager.KeyboardFocusControl?.GetType().Name ?? "none"
        );

        TouchInput.Trace.Clear();
        await Hold(host, Client(new Vector2(60, 60)), TouchInput.LongPressMs + 150);
        await Frames(host, 5);

        Check(
            "a long press over a gump became a right click",
            TouchInput.Trace.Contains("long press -> right click"),
            string.Join(" | ", TouchInput.Trace)
        );

        // --- in the world --------------------------------------------------

        // The login itself goes through the mouse path, which is what the
        // input probe already proves; the layer steps aside for it.
        TouchInput.Enabled = false;
        await InputProbe.EnterTheWorld(host, 0);
        TouchInput.Enabled = true;

        Game.World world = GUO.Client.Game.UO.World;

        Check(
            "the character is in the world",
            world.InGame,
            world.Player == null ? "no player -- is the dev shard running?" : $"{world.Player.Name} at {world.Player.X},{world.Player.Y}"
        );

        if (!world.InGame)
        {
            Finish();

            return;
        }

        await Frames(host, 120);

        await WalkCheck(host, world);
        await DoubleTapCheck(host, world);
        await PinchCheck(host);
        await BarCheck(host);
        await ParkCheck(host);
        await TargetTapCheck(host, world);
        await MacroRowCheck(host, world);
        await FlickCheck(host, world);
        await OptionsTouchCheck(host, world);
        // The long press is checked with hold-and-flick off, the way a player
        // who set every direction to "Do nothing" has it.
        {
            var prof = Configuration.ProfileManager.CurrentProfile;
            (int u, int d, int l, int r) kept = (prof.FlickUp, prof.FlickDown, prof.FlickLeft, prof.FlickRight);
            prof.FlickUp = prof.FlickDown = prof.FlickLeft = prof.FlickRight = 0;
            await LongPressCheck(host);
            (prof.FlickUp, prof.FlickDown, prof.FlickLeft, prof.FlickRight) = kept;
        }
        await GumpScaleCheck(host, world);
        await ScaledContainerCheck(host, world);
        await BarCostCheck(host);

        Finish();
    }

    /// <summary>Hold a finger on the world and see the character go that way.</summary>
    private static async System.Threading.Tasks.Task WalkCheck(Node host, Game.World world)
    {
        int startX = world.Player.X, startY = world.Player.Y;
        bool rightHeld = false;
        bool moved = false;

        foreach (Vector2 diagonal in new[] { new Vector2(1, 1), new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1) })
        {
            TouchInput.Trace.Clear();

            Vector2 at = WorldPoint(diagonal, 0.6f);

            Touch(0, at, true);

            for (int i = 0; i < 90; i++)
            {
                await Frames(host, 1);
                rightHeld |= Mouse.RButtonPressed;
            }

            Touch(0, at, false);
            await Frames(host, 20);

            GD.Print(
                $"[GUO] touch probe: held {diagonal.X},{diagonal.Y} -> player at {world.Player.X},{world.Player.Y}, "
                + $"steps {world.Player.Walker.StepsCount}; trace: {string.Join(" | ", TouchInput.Trace)}"
            );

            moved |= world.Player.X != startX || world.Player.Y != startY;

            if (moved)
            {
                break;
            }
        }

        Check("a held finger on the world became a held right button", rightHeld);
        Check("the character walked toward the finger", moved, $"from {startX},{startY} to {world.Player.X},{world.Player.Y}");
    }

    /// <summary>Two quick taps on the character open the paperdoll, as a double click does.</summary>
    private static async System.Threading.Tasks.Task DoubleTapCheck(Node host, Game.World world)
    {
        TouchInput.Trace.Clear();

        Vector2 at = WorldPoint(Vector2.Zero, 0);

        // The character stands a little above the centre of the camera; the
        // body is the safer target than the feet.
        at.Y -= 10 * GUO.Client.Game.DpiScale;

        await Tap(host, at);
        await Frames(host, 4);
        await Tap(host, at);
        await Frames(host, 60);

        Check(
            "a second quick tap became a double click",
            TouchInput.Trace.Contains("second tap -> left press (double click)"),
            string.Join(" | ", TouchInput.Trace)
        );
        Check("the paperdoll opened", UIManager.GetGump<PaperDollGump>() != null);
    }

    /// <summary>Two fingers spreading zoom the camera in.</summary>
    private static async System.Threading.Tasks.Task PinchCheck(Node host)
    {
        TouchInput.Trace.Clear();

        Renderer.Camera camera = GUO.Client.Game.Scene.Camera;
        float before = camera.Zoom;

        // Spread the fingers to zoom in, unless the profile left the camera
        // at its closest already -- then bring them together and zoom out.
        // Zoom is a divisor here: smaller is closer, and ZoomIn subtracts.
        bool spread = before > camera.ZoomMin + 0.001f;
        float sign = spread ? 1 : -1;
        Vector2 centre = WorldPoint(Vector2.Zero, 0);
        float start = spread ? 40 : 160;
        Vector2 a = centre + new Vector2(-start, 0);
        Vector2 b = centre + new Vector2(start, 0);

        Touch(0, a, true);
        await Frames(host, 2);
        Touch(1, b, true);
        await Frames(host, 2);

        for (int step = 1; step <= 8; step++)
        {
            Vector2 na = centre + new Vector2(-start - sign * step * 15, 0);
            Vector2 nb = centre + new Vector2(start + sign * step * 15, 0);

            Drag(0, na, na - a);
            Drag(1, nb, nb - b);
            a = na;
            b = nb;

            await Frames(host, 2);
        }

        Touch(1, b, false);
        Touch(0, a, false);
        await Frames(host, 10);

        float after = camera.Zoom;

        Check(
            "a pinch became ctrl+wheel",
            TouchInput.Trace.Contains(spread ? "pinch out -> ctrl+wheel up (zoom in)" : "pinch in -> ctrl+wheel down (zoom out)"),
            string.Join(" | ", TouchInput.Trace)
        );
        Check("the camera zoom changed", !Mathf.IsEqualApprox(before, after), $"{before:F2} -> {after:F2}");

        // Put it back: the zoom is saved with the profile, and the next run
        // should start where this one did.
        camera.Zoom = before;
    }

    /// <summary>A tap on the bar's backpack button opens the backpack.</summary>
    private static async System.Threading.Tasks.Task BarCheck(Node host)
    {
        TouchInput.Trace.Clear();

        TouchGumpBar bar = TouchInput.Bar;

        Check("the gump bar is shown in the world", bar != null && bar.Shown);

        if (bar == null)
        {
            return;
        }

        Rect2 button = bar.ButtonRect("backpack");

        await Tap(host, button.Position + button.Size / 2);
        await Frames(host, 60);

        Check(
            "the tap landed on the bar",
            TouchInput.Trace.Contains("bar -> backpack"),
            string.Join(" | ", TouchInput.Trace)
        );
        Check("the backpack opened", UIManager.GetGump<ContainerGump>() != null);
    }

    /// <summary>
    /// What the bar costs a frame, with the macro row up and the world
    /// running: its own _Process and _Draw time and how often it drew, next
    /// to the whole frame's process time. Printed for the before/after
    /// comparison; the check is only that it was measured.
    /// </summary>
    private static async System.Threading.Tasks.Task BarCostCheck(Node host)
    {
        TouchGumpBar bar = TouchInput.Bar;

        if (bar == null || !bar.Shown)
        {
            Check("the bar's frame cost is measured", false, "no bar");
            return;
        }

        const int frames = 600;
        double process = 0;
        TouchGumpBar.CostTicks = 0;
        TouchGumpBar.DrawCount = 0;

        for (int i = 0; i < frames; i++)
        {
            await Frames(host, 1);
            process += Performance.GetMonitor(Performance.Monitor.TimeProcess);
        }

        double barUs = TouchGumpBar.CostTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
        string line = $"bar {barUs:F1} us/frame, {TouchGumpBar.DrawCount} draws in {frames} frames, "
            + $"frame process {process / frames * 1000:F2} ms, {UIManager.Gumps.Count} gumps, row {(bar.RowShown ? "up" : "down")}";
        GD.Print($"[GUO] touch probe: bar cost: {line}");
        Check("the bar's frame cost is measured", TouchGumpBar.DrawCount >= 0, line);
    }

    /// <summary>
    /// After a tap, once a second tap can no longer come, the pointer leaves
    /// the screen, so nothing stays hovered: no tooltip, no lit slot.
    /// </summary>
    private static async System.Threading.Tasks.Task ParkCheck(Node host)
    {
        TouchInput.Trace.Clear();

        await Tap(host, WorldPoint(new Vector2(-1, -1), 0.4f));
        await Frames(host, 5);

        bool stillThere = Input.Mouse.Position.X >= 0;

        ulong until = Godot.Time.GetTicksMsec() + (ulong)Input.Mouse.MOUSE_DELAY_DOUBLE_CLICK + 250;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }

        Check(
            "the pointer stays for a second tap, then leaves the screen",
            stillThere && Input.Mouse.Position.X < 0 && TouchInput.Trace.Contains("finger gone -> pointer parked"),
            $"at {Input.Mouse.Position.X},{Input.Mouse.Position.Y} | {string.Join(" | ", TouchInput.Trace)}"
        );
        Check("nothing is hovered after it leaves", UIManager.MouseOverControl == null && Game.SelectedObject.Object == null);
    }

    /// <summary>
    /// A tap on the world answers a target cursor, even one so short that the
    /// finger is up before the client has picked the tile under it.
    /// </summary>
    private static async System.Threading.Tasks.Task TargetTapCheck(Node host, Game.World world)
    {
        TouchInput.Trace.Clear();

        // A client-side cursor with no server behind it: what a tap does to
        // it is all the check needs, and the server ignores the answer.
        world.TargetManager.SetTargeting(CursorTarget.Position, 0, TargetType.Neutral);
        await Frames(host, 5);

        bool up = world.TargetManager.IsTargeting;

        // Down and up in the same frame, as adb sends a tap and as a quick
        // finger can: the client has not yet picked what is under it.
        Vector2 at = WorldPoint(Vector2.Zero, 0f);
        Touch(0, at, true);
        Touch(0, at, false);
        await Frames(host, 12);

        Check(
            "a tap on the world answers a target cursor",
            up && !world.TargetManager.IsTargeting,
            $"cursor up before {up}, after {world.TargetManager.IsTargeting} | {string.Join(" | ", TouchInput.Trace)}"
        );

        if (world.TargetManager.IsTargeting)
        {
            world.TargetManager.CancelTarget();
        }
    }

    /// <summary>
    /// The chevron and the macro row: the row comes up on entering War mode,
    /// a macro button runs its macro (War/Peace), the chevron hides the row,
    /// a hidden row stays hidden through the next War mode, and the chevron
    /// brings it back. Leaves the row up for the photograph.
    /// </summary>
    private static async System.Threading.Tasks.Task MacroRowCheck(Node host, Game.World world)
    {
        TouchGumpBar bar = TouchInput.Bar;
        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;

        if (bar == null || profile == null)
        {
            Check("the macro row", false, "no bar or no profile");

            return;
        }

        // A desktop profile does not have the mobile default; the probe
        // turns it on for this run, as Options would.
        profile.TouchMacroRow = true;

        if (world.Player.InWarMode)
        {
            Game.GameActions.ToggleWarMode(world.Player);
            await Frames(host, 60);
        }

        // A character that logged in at war has already had its row come up.
        bar.ResetSession();
        await Frames(host, 5);
        Check("the chevron is shown and the row is down", bar.ChevronShown && !bar.RowShown);

        Rect2 chevronDown = bar.ChevronRect();
        TouchInput.Trace.Clear();
        Game.GameActions.ToggleWarMode(world.Player);
        await Frames(host, 60);

        Check("the chevron stays where it was when the row opens", bar.ChevronRect() == chevronDown,
            $"{chevronDown} -> {bar.ChevronRect()}");

        Check(
            "the row comes up on entering War mode",
            world.Player.InWarMode && bar.RowShown,
            $"war {world.Player.InWarMode}, row {bar.RowShown} | {string.Join(" | ", TouchInput.Trace)}"
        );

        Rect2 war = bar.ButtonRect("m:war");
        await Tap(host, war.Position + war.Size / 2);
        await Frames(host, 60);

        Check("the War/Peace button ran its macro", !world.Player.InWarMode, string.Join(" | ", TouchInput.Trace));

        Rect2 chevron = bar.ChevronRect();
        await Tap(host, chevron.Position + chevron.Size / 2);
        await Frames(host, 5);

        Check("the chevron hides the row", !bar.RowShown && bar.HiddenThisSession);

        Game.GameActions.ToggleWarMode(world.Player);
        await Frames(host, 60);

        Check("a hidden row stays down on entering War mode", world.Player.InWarMode && !bar.RowShown);

        Game.GameActions.ToggleWarMode(world.Player);
        await Frames(host, 60);

        chevron = bar.ChevronRect();
        await Tap(host, chevron.Position + chevron.Size / 2);
        await Frames(host, 5);

        Check("the chevron brings the row back", bar.RowShown, string.Join(" | ", TouchInput.Trace));
    }

    /// <summary>
    /// Hold and flick (S12): a still hold on a gump's frame lifts it without
    /// pressing a button; a release inside the threshold does nothing; each
    /// direction does its configured action. On a single screen up and down
    /// fit the gump to the screen.
    /// </summary>
    private static async System.Threading.Tasks.Task FlickCheck(Node host, Game.World world)
    {
        var p = Configuration.ProfileManager.CurrentProfile;
        (int u, int d, int l, int r) saved = (p.FlickUp, p.FlickDown, p.FlickLeft, p.FlickRight);
        p.FlickUp = (int)FlickAction.ToTopScreen; p.FlickDown = (int)FlickAction.ToBottomScreen;
        p.FlickLeft = (int)FlickAction.Close; p.FlickRight = (int)FlickAction.Reset;

        try
        {
            PaperDollGump g = await FreshPaperdoll(host, world);
            Vector2? at = g == null ? null : await FlickPoint(host, g);
            Check("a still point on the paperdoll's frame accepts a flick hold", at != null);

            if (at == null)
            {
                return;
            }

            // Lift, then let go inside the threshold: nothing happens.
            TouchInput.Trace.Clear();
            await Lift(host, at.Value);
            bool liftedNoButton = GumpFlick.Lifted == g && !Mouse.LButtonPressed && !Mouse.RButtonPressed
                && !GUO.Client.Game.UO.GameCursor.ItemHold.Enabled;
            Check("a still hold lifts the gump without pressing a button", liftedNoButton,
                string.Join(" | ", TouchInput.Trace));
            Touch(0, at.Value + new Vector2(6, 0), false);
            await Frames(host, 5);
            Check("letting go in place opens the window menu, and nothing else",
                GumpFlick.LastResult.Contains("menu") && !g.IsDisposed && GumpFlick.Lifted == null
                && WindowMenu.IsOpen && WindowMenu.Target == g, GumpFlick.LastResult);
            WindowMenu.Close();
            await Frames(host, 5);

            // Up and down on a single screen: fit to screen.
            float before = g.PresentationScale;
            await Flick(host, at.Value, new Vector2(0, -160));
            Check("flick up on a single screen fits the gump to the screen",
                GumpFlick.LastResult == "flick up -> ToTopScreen" && g.PresentationScale > before,
                $"{GumpFlick.LastResult}, scale {before} -> {g.PresentationScale}");

            // Right: reset size.
            at = await FlickPoint(host, g);
            await Flick(host, at.Value, new Vector2(160, 0));
            Check("flick right resets the size", GumpFlick.LastResult == "flick right -> Reset" && g.PresentationScale == 1f,
                $"{GumpFlick.LastResult}, scale {g.PresentationScale}");

            // Down: fit again (single screen).
            at = await FlickPoint(host, g);
            await Flick(host, at.Value, new Vector2(0, 160));
            Check("flick down on a single screen fits the gump too",
                GumpFlick.LastResult == "flick down -> ToBottomScreen" && g.PresentationScale > 1f, GumpFlick.LastResult);
            GumpPresentation.Reset(g);

            // Left: close, with a Reopen toast that brings it back.
            at = await FlickPoint(host, g);
            await Flick(host, at.Value, new Vector2(-160, 0));
            UndoToast toast = UIManager.GetGump<UndoToast>();
            Check("flick left closes the gump and offers Reopen",
                GumpFlick.LastResult == "flick left -> Close" && g.IsDisposed && toast != null, GumpFlick.LastResult);
            toast?.OnButtonClick(1);
            await Frames(host, 60);
            Check("Reopen brings it back", UIManager.GetGump<PaperDollGump>(world.Player.Serial) != null);

            // Minimise to the touch bar: hidden, a chip on the bar, and a tap
            // on the chip shows it again where it was, at the size it was.
            g = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
            p.FlickLeft = (int)FlickAction.Minimise;
            GumpPresentation.SetScale(g, 1.25f, new Compat.Point(g.X, g.Y));
            (int mx, int my, float ms) = (g.X, g.Y, g.PresentationScale);
            at = await FlickPoint(host, g);
            await Flick(host, at.Value, new Vector2(-160, 0));
            Rect2? chip = TouchInput.Bar.ChipRect(g);
            Check("flick left set to Minimise hides the gump and puts a chip on the touch bar",
                GumpFlick.LastResult == "flick left -> Minimise" && !g.IsVisible && !g.IsDisposed && chip != null,
                $"{GumpFlick.LastResult}, visible {g.IsVisible}, chip {chip}");

            if (chip != null)
            {
                await Tap(host, chip.Value.GetCenter());
                await Frames(host, 5);
            }

            Check("a tap on the chip restores it at the same place and size, and the chip goes",
                g.IsVisible && g.X == mx && g.Y == my && g.PresentationScale == ms && TouchInput.Bar.ChipRect(g) == null,
                $"visible {g.IsVisible}, at {g.X},{g.Y} (was {mx},{my}), scale {g.PresentationScale} (was {ms}) | {string.Join(" | ", TouchInput.Trace)}");
            GumpPresentation.Reset(g);
        }
        finally
        {
            GumpFlick.Cancel();
            (p.FlickUp, p.FlickDown, p.FlickLeft, p.FlickRight) = saved;
        }
    }

    /// <summary>
    /// The Odin findings (C2): a gump is kept above the touch bar, and a
    /// vertical swipe inside a scroll area (the Options pages) scrolls it
    /// instead of dragging the gump.
    /// </summary>
    private static async System.Threading.Tasks.Task OptionsTouchCheck(Node host, Game.World world)
    {
        Game.GameActions.OpenSettings(world);
        await Frames(host, 60);
        OptionsGump options = UIManager.GetGump<OptionsGump>();

        if (options == null)
        {
            Check("Options opens for the touch checks", false);
            return;
        }

        try
        {
            int bottom = GumpPresentation.DisplayBounds(false).Height;
            options.Y = bottom - 20;
            await Frames(host, 5);
            Check("a gump pushed under the touch bar is moved back above it",
                options.Y + options.Height <= bottom, $"bottom edge {options.Y + options.Height}, bar at {bottom}");

            options.Y = 0;
            await Frames(host, 5);

            // The first scroll area on the open page that has more than it shows.
            Game.UI.Controls.ScrollArea area = null;
            foreach (Game.UI.Controls.Control c in options.FindControls<Game.UI.Controls.ScrollArea>())
            {
                if (c.IsVisible && c.Page == options.ActivePage && c is Game.UI.Controls.ScrollArea a
                    && a.ScrollMaxValue > a.ScrollMinValue) { area = a; break; }
            }

            if (area == null)
            {
                Check("an Options page with a scroll area", false);
                return;
            }

            int before = area.ScrollValue;
            int gx = options.X, gy = options.Y;
            Vector2 start = Client(new Vector2(area.ScreenCoordinateX + 60, area.ScreenCoordinateY + area.Height * 0.7f));
            TouchInput.Trace.Clear();
            Touch(0, start, true);
            await Frames(host, 2);

            for (int i = 1; i <= 8; i++)
            {
                Drag(0, start + new Vector2(0, -25 * i), new Vector2(0, -25));
                await Frames(host, 1);
            }

            Touch(0, start + new Vector2(0, -200), false);
            await Frames(host, 10);

            Check("a vertical swipe inside an Options page scrolls it and leaves the gump where it was",
                area.ScrollValue > before && options.X == gx && options.Y == gy && !Mouse.LButtonPressed,
                $"scroll {before} -> {area.ScrollValue}, gump {gx},{gy} -> {options.X},{options.Y} | {string.Join(" | ", TouchInput.Trace)}");
        }
        finally
        {
            options.Dispose();
            await Frames(host, 5);
        }
    }

    private static async System.Threading.Tasks.Task<PaperDollGump> FreshPaperdoll(Node host, Game.World world)
    {
        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        await Frames(host, 5);
        Game.GameActions.OpenPaperdoll(world, world.Player);
        await Frames(host, 40);
        PaperDollGump g = UIManager.GetGump<PaperDollGump>(world.Player.Serial);

        if (g != null)
        {
            g.X = 120; g.Y = 120; g.PresentationScale = 1f; g.PresentationLocked = false;
            g.BringOnTop();
            await Frames(host, 5);
        }

        return g;
    }

    /// <summary>A point on the gump where GumpFlick would start, in window pixels, or null.</summary>
    private static async System.Threading.Tasks.Task<Vector2?> FlickPoint(Node host, Game.UI.Gumps.Gump g)
    {
        for (int fy = 1; fy < 10; fy++)
        {
            for (int fx = 1; fx < 8; fx++)
            {
                var local = new Vector2(g.X + GumpPresentation.Width(g) * fx / 8f, g.Y + GumpPresentation.Height(g) * fy / 10f);
                Vector2 at = Client(local);
                GodotInput.Handle(new InputEventMouseMotion { Position = at });
                await Frames(host, 2);

                if (GumpFlick.CanStart(out Game.UI.Gumps.Gump lift) && lift == g)
                {
                    return at;
                }
            }
        }

        return null;
    }

    private static async System.Threading.Tasks.Task Lift(Node host, Vector2 at)
    {
        Touch(0, at, true);
        ulong until = Godot.Time.GetTicksMsec() + (ulong)GumpFlick.HoldMs + 80;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }
    }

    private static async System.Threading.Tasks.Task Flick(Node host, Vector2 at, Vector2 by)
    {
        TouchInput.Trace.Clear();
        await Lift(host, at);

        for (int i = 1; i <= 4; i++)
        {
            Drag(0, at + by * i / 4f, by / 4f);
            await Frames(host, 1);
        }

        Touch(0, at + by, false);
        await Frames(host, 20);
        GD.Print($"[GUO] touch probe: {GumpFlick.LastResult} | {string.Join(" | ", TouchInput.Trace)}");
    }

    /// <summary>A long press on the paperdoll's frame closes it, as a right click does.</summary>
    private static async System.Threading.Tasks.Task LongPressCheck(Node host)
    {
        PaperDollGump paperdoll = UIManager.GetGump<PaperDollGump>();

        if (paperdoll == null)
        {
            Check("a long press on a gump closed it", false, "no paperdoll to press");

            return;
        }

        // Somewhere on the paperdoll's own art: not on an equipment slot,
        // which a hold picks up, and not under another gump -- the minimap
        // and the status bar open over its top-left corner.
        Vector2 at = default;
        bool found = false;

        foreach (Vector2 candidate in new[]
        {
            new Vector2(paperdoll.X + paperdoll.Width - 12, paperdoll.Y + paperdoll.Height / 2),
            new Vector2(paperdoll.X + paperdoll.Width / 2, paperdoll.Y + paperdoll.Height - 12),
            new Vector2(paperdoll.X + 12, paperdoll.Y + paperdoll.Height / 2),
            new Vector2(paperdoll.X + paperdoll.Width / 2, paperdoll.Y + 12),
        })
        {
            at = Client(candidate);

            // Straight to the mouse path: the touch layer would swallow a
            // mouse event from the queue, and a finger cannot hover.
            GodotInput.Handle(new InputEventMouseMotion { Position = at });
            await Frames(host, 3);

            Game.UI.Controls.Control over = UIManager.MouseOverControl;

            if (over != null && over.RootParent == paperdoll
                && over is not Game.UI.Controls.ItemGump
                && !(over is Game.UI.Controls.GumpPic && over.Parent is Game.UI.Controls.PaperDollInteractable))
            {
                found = true;

                break;
            }
        }

        Check("a point on the paperdoll's frame was found", found, $"{at.X},{at.Y}");

        TouchInput.Trace.Clear();

        await Hold(host, at, TouchInput.LongPressMs + 150);
        await Frames(host, 20);

        Check(
            "a long press over the paperdoll became a right click",
            TouchInput.Trace.Contains("long press -> right click"),
            string.Join(" | ", TouchInput.Trace)
        );
        Check("the paperdoll closed", paperdoll.IsDisposed || UIManager.GetGump<PaperDollGump>() == null);
    }

    private static async System.Threading.Tasks.Task GumpScaleCheck(Node host, Game.World world)
    {
        Game.GameActions.OpenPaperdoll(world, world.Player);
        await Frames(host, 40);
        PaperDollGump g = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        Check("scalable paperdoll is available", g != null);
        if (g == null) return;
        int oldX = g.X, oldY = g.Y;
        float oldScale = g.PresentationScale;
        bool oldLock = g.PresentationLocked;
        var hit = new Game.UI.Controls.HitBox(40, 70, 160, 60);
        int clicks = 0, clickX = -1, clickY = -1;
        Compat.Point observed = default;
        hit.MouseUp += (_, e) => { clicks++; clickX = e.X; clickY = e.Y; observed = Mouse.Position; };
        g.Add(hit);
        g.BringOnTop();
        g.X = 80; g.Y = 80; g.PresentationLocked = false; g.PresentationScale = 1f;
        float zoom = GUO.Client.Game.Scene.Camera.Zoom;
        try
        {
            await Frames(host, 35);
            Vector2 a = Client(new Vector2(g.X + 60, g.Y + 90));
            Vector2 b = Client(new Vector2(g.X + 140, g.Y + 90));
            Touch(0, a, true); Touch(1, b, true);
            await Frames(host, 2);
            Drag(1, b + Client(new Vector2(40, 0)), Client(new Vector2(40, 0)));
            await Frames(host, 3);
            float scaled = g.PresentationScale;
            Check("pinch scales the owning gump", scaled > 1.3f && scaled < 1.7f, $"scale {scaled}");
            Check("gump pinch does not zoom the world", GUO.Client.Game.Scene.Camera.Zoom == zoom);
            // Native magnify for this same gesture must not apply a second scale.
            Godot.Input.ParseInputEvent(new InputEventMagnifyGesture { Factor = 1.5f, Position = (a + b) / 2 });
            await Frames(host, 2);
            Check("raw and native pinch do not apply twice", g.PresentationScale == scaled);
            Touch(0, a, false); // primary lifts first
            await Frames(host, 2);
            Drag(1, b, -Client(new Vector2(40, 0)));
            Touch(1, b, false);
            await Frames(host, 3);
            Check("pinch release does not click", clicks == 0 && !Mouse.LButtonPressed && !Mouse.RButtonPressed);

            var local = new Compat.Point(g.X + hit.X + 30, g.Y + hit.Y + 20);
            Compat.Point screen = GumpPresentation.ToScreen(g, local);
            await Tap(host, Client(new Vector2(screen.X, screen.Y)));
            Check("scaled hit test and event coordinates agree", clicks == 1 && System.Math.Abs(clickX - 30) <= 1
                && System.Math.Abs(clickY - 20) <= 1, $"clicks {clicks}, local {clickX},{clickY}");
            Check("legacy pointer reads use inverse scale", System.Math.Abs(observed.X - local.X) <= 1
                && System.Math.Abs(observed.Y - local.Y) <= 1);

            // Leave a real rendered frame for visual QA of the transform, fonts and chrome.
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string screenshotDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                ProjectSettings.GlobalizePath("res://"), "..", "..", "build", "screenshots"));
            System.IO.Directory.CreateDirectory(screenshotDir);
            using (Image frame = host.GetViewport().GetTexture().GetImage())
            using (Image region = frame.GetRegion(new Rect2I(0, 0, System.Math.Min(800, frame.GetWidth()), System.Math.Min(650, frame.GetHeight()))))
                region.SavePng(System.IO.Path.Combine(screenshotDir, "gump_scale_150.png"));

            await Frames(host, 35);
            g.PresentationLocked = true;
            a = Client(new Vector2(screen.X - 10, screen.Y)); b = Client(new Vector2(screen.X + 20, screen.Y));
            Touch(0, a, true); Touch(1, b, true);
            Drag(1, b + new Vector2(80, 0), new Vector2(80, 0));
            await Frames(host, 3);
            Touch(1, b, false); Touch(0, a, false);
            await Frames(host, 2);
            Check("locked gump consumes pinch without world zoom", g.PresentationScale == scaled
                && GUO.Client.Game.Scene.Camera.Zoom == zoom && clicks == 1);

            // Invalid/mixed gesture cannot fall through to a world zoom or activate a control.
            await Frames(host, 35);
            g.PresentationLocked = false;
            a = Client(new Vector2(screen.X, screen.Y)); b = Client(new Vector2(600, 420));
            Touch(0, a, true); Touch(1, b, true);
            Drag(1, b + new Vector2(80, 0), new Vector2(80, 0));
            await Frames(host, 3);
            Touch(1, b, false); Touch(0, a, false);
            await Frames(host, 2);
            Check("mixed gump/world pinch is ignored", g.PresentationScale == scaled
                && GUO.Client.Game.Scene.Camera.Zoom == zoom && clicks == 1);

            await Frames(host, 35);
            Touch(0, a, true);
            await Frames(host, 2);
            Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = a, Canceled = true });
            await Frames(host, 2);
            Touch(0, a, false);
            await Frames(host, 2);
            Check("cancelled touch never clicks or leaves a button held", clicks == 1 && !Mouse.LButtonPressed && !Mouse.RButtonPressed);

            var buffer = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using (var writer = new System.Xml.XmlTextWriter(buffer))
            { writer.WriteStartElement("gump"); g.Save(writer); writer.WriteEndElement(); }
            var xml = new System.Xml.XmlDocument(); xml.LoadXml(buffer.ToString());
            Check("scale is included in saved gump layout", xml.DocumentElement.GetAttribute("ui_scale")
                == scaled.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var restored = new Gump(world, 0, 0);
            restored.Restore(xml.DocumentElement);
            Check("saved scale restores without changing server layout", restored.PresentationScale == scaled);
            restored.Dispose();

            await Frames(host, 35);
            Compat.Rectangle gem = GumpPresentation.GemRect(g);

            // Hidden by default on touch: a tap where the handle would be opens nothing.
            var prof = Configuration.ProfileManager.CurrentProfile;
            bool shown = prof.ShowWindowHandles;
            prof.ShowWindowHandles = false;
            await Frames(host, 5);
            Check("window handles are hidden by default", GumpPresentation.GemAlpha(g) == 0f
                && !GumpPresentation.OpenGem(new Compat.Point(gem.X + 14, gem.Y + 14)));

            // With "Show window handles" on, the handle opens the menu as before.
            prof.ShowWindowHandles = true;
            await Frames(host, 5);
            TouchInput.Trace.Clear();
            await Tap(host, Client(new Vector2(gem.X + 14, gem.Y + 14)));
            prof.ShowWindowHandles = shown;
            Check("window gem opens size and screen controls", WindowMenu.IsOpen && WindowMenu.Target == g,
                $"gem {gem.X},{gem.Y}, disposed {g.IsDisposed}, modal {UIManager.IsModalOpen}, held {GUO.Client.Game.UO.GameCursor.ItemHold.Enabled}, "
                + $"top {UIManager.Gumps.First?.Value.GetType().Name} | {string.Join(" | ", TouchInput.Trace)}");
            if (WindowMenu.IsOpen)
            {
                await Frames(host, 12); // the card's open animation
                float before = g.PresentationScale;
                Vector2? plus = WindowMenu.ButtonCentre("+");
                if (plus != null) await Tap(host, Client(plus.Value));
                await Frames(host, 5);
                Check("the window menu's + steps the size by 25%", plus != null && System.Math.Abs(g.PresentationScale - (float)(System.Math.Round((before + 0.25f) * 4) / 4)) < 0.01f,
                    $"{before} -> {g.PresentationScale}");
                Vector2? reset = WindowMenu.ButtonCentre("Reset size");
                if (reset != null) await Tap(host, Client(reset.Value));
                await Frames(host, 5);
                Check("Reset size restores original scale", g.PresentationScale == 1f, $"scale {g.PresentationScale}");
                Rect2 card = WindowMenu.CardRect;
                await Tap(host, Client(new Vector2(card.Position.X - 60 > 0 ? card.Position.X - 60 : card.End.X + 60, card.Position.Y + 20)));
                Check("a tap outside the card closes the menu, and does nothing else", !WindowMenu.IsOpen && !g.IsDisposed);
            }
        }
        finally
        {
            hit.Dispose();
            WindowMenu.Close();
            g.X = oldX; g.Y = oldY; g.PresentationScale = oldScale; g.PresentationLocked = oldLock;
        }
    }

    private static async System.Threading.Tasks.Task ScaledContainerCheck(Node host, Game.World world)
    {
        var bag = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
        Game.GameActions.OpenBackpack(world);
        await Frames(host, 35);
        ContainerGump pack = bag == null ? null : UIManager.GetGump<ContainerGump>(bag.Serial);
        if (pack == null)
        {
            Check("classic container scaling fixture", false, "requires a classic backpack for drop-coordinate parity");
            return;
        }
        Game.GameObjects.Item item = null;
        for (Game.LinkedObject next = bag.Items; next != null; next = next.Next)
            if (next is Game.GameObjects.Item candidate && candidate.Amount == 1) { item = candidate; break; }
        Check("container has an item for drop-coordinate parity", item != null);
        if (item == null) return;
        int oldX = pack.X, oldY = pack.Y, itemX = item.X, itemY = item.Y;
        float oldScale = pack.PresentationScale;
        bool locked = pack.PresentationLocked;
        uint serial = item.Serial;
        pack.X = 500; pack.Y = 100; pack.PresentationScale = 1; pack.PresentationLocked = false;
        pack.BringOnTop();
        try
        {
            // Find actual bare background rather than accidentally dropping into another item.
            Compat.Point? destination = null;
            for (int y = pack.Height / 4; y < pack.Height * 3 / 4 && destination == null; y += 18)
                for (int x = pack.Width / 4; x < pack.Width * 3 / 4; x += 18)
                {
                    var p = new Compat.Point(pack.X + x, pack.Y + y);
                    Game.UI.Controls.Control hit = null;
                    pack.HitTest(p, ref hit);
                    if (hit?.GetType().Name == "GumpPicContainer") { destination = p; break; }
                }
            Check("bare container destination is available", destination != null);
            if (destination == null) return;
            Compat.Point relative = destination.Value - new Compat.Point(pack.X, pack.Y);
            async System.Threading.Tasks.Task DropAt(float scale)
            {
                TouchInput.CancelGesture();
                GumpPresentation.SetScale(pack, scale, new Compat.Point(pack.X, pack.Y));
                Game.GameActions.PickUp(world, serial, 0, 0, 1);
                await Frames(host, 30);
                var p = GumpPresentation.ToScreen(pack, new Compat.Point(pack.X + relative.X, pack.Y + relative.Y));
                GodotInput.Handle(new InputEventMouseMotion { Position = Client(new Vector2(p.X, p.Y)) });
                await Frames(host, 5);
                await Tap(host, Client(new Vector2(p.X, p.Y)));
                await Frames(host, 35);
            }
            await DropAt(1);
            world.Items.TryGetValue(serial, out var baseline);
            bool firstDrop = baseline != null && baseline.Container == bag.Serial && !GUO.Client.Game.UO.GameCursor.ItemHold.Enabled;
            int expectedX = baseline?.X ?? -1, expectedY = baseline?.Y ?? -1;
            Check("unscaled reference item drop completes", firstDrop);
            if (!firstDrop) return;
            await DropAt(1.5f);
            world.Items.TryGetValue(serial, out var scaled);
            Check("scaled drop sends the same container coordinates", scaled != null && scaled.Container == bag.Serial
                && System.Math.Abs(scaled.X - expectedX) <= 1 && System.Math.Abs(scaled.Y - expectedY) <= 1
                && !GUO.Client.Game.UO.GameCursor.ItemHold.Enabled,
                $"reference {expectedX},{expectedY}, scaled {scaled?.X},{scaled?.Y}");
        }
        finally
        {
            // Restore the dev character's item and window rather than leaving test state behind.
            TouchInput.CancelGesture();
            if (!GUO.Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                Game.GameActions.PickUp(world, serial, 0, 0, 1);
                await Frames(host, 20);
            }
            if (GUO.Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                Game.GameActions.DropItem(serial, itemX, itemY, 0, bag.Serial);
                await Frames(host, 25);
            }
            pack.X = oldX; pack.Y = oldY; pack.PresentationScale = oldScale; pack.PresentationLocked = locked;
        }
    }

    // --- fingers ---------------------------------------------------------

    private static void Touch(int index, Vector2 at, bool pressed) =>
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = index, Position = at, Pressed = pressed });

    private static void Drag(int index, Vector2 at, Vector2 relative) =>
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = index, Position = at, Relative = relative });

    private static async System.Threading.Tasks.Task Tap(Node host, Vector2 at)
    {
        Touch(0, at, true);
        await Frames(host, 3);
        Touch(0, at, false);

        // A tap's button comes up two frames after the finger (TouchInput.Tap);
        // give the client a frame beyond that to act on the release.
        await Frames(host, 5);
    }

    private static async System.Threading.Tasks.Task Hold(Node host, Vector2 at, int milliseconds)
    {
        Touch(0, at, true);

        ulong until = Godot.Time.GetTicksMsec() + (ulong)milliseconds;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }

        Touch(0, at, false);
        await Frames(host, 2);
    }

    // --- geometry --------------------------------------------------------

    /// <summary>Client pixels to viewport pixels, which is what a finger reports.</summary>
    private static Vector2 Client(Vector2 clientPixels) => clientPixels * GUO.Client.Game.DpiScale;

    /// <summary>
    /// A point in the world view: the centre of the camera, pushed along a
    /// direction by a fraction of the half-extent, in viewport pixels.
    /// </summary>
    private static Vector2 WorldPoint(Vector2 direction, float fraction)
    {
        Compat.Rectangle bounds = GUO.Client.Game.Scene.Camera.Bounds;
        var centre = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        float half = System.Math.Min(bounds.Width, bounds.Height) / 2f - 24;

        return Client(centre + direction * (half * fraction));
    }

    private static async System.Threading.Tasks.Task Frames(Node host, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    // --- verdict ---------------------------------------------------------

    private static void Check(string what, bool ok, string detail = null)
    {
        Checks.Add((what, ok));
        GD.Print($"[GUO] touch check: {(ok ? "ok  " : "FAIL")} {what}" + (detail == null ? "" : $" -- {detail}"));
    }

    private static void Finish()
    {
        int passed = 0;

        foreach ((string what, bool ok) in Checks)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                GD.PrintErr($"[GUO] touch probe FAILED: {what}");
            }
        }

        Passed = passed == Checks.Count;
        GD.Print($"[GUO] touch probe: {passed}/{Checks.Count} checks passed");
    }
}
