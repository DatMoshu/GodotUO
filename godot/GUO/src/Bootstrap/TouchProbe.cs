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
        await MacroRowCheck(host, world);
        await LongPressCheck(host);

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

        await Frames(host, 5);
        Check("the chevron is shown and the row is down", bar.ChevronShown && !bar.RowShown);

        TouchInput.Trace.Clear();
        Game.GameActions.ToggleWarMode(world.Player);
        await Frames(host, 60);

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
        await Frames(host, 2);
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
