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

        // The checks that open Options mean the classic gump; Modern Options
        // (ADR-0024) has its own check, which turns it on.
        Configuration.ProfileManager.CurrentProfile.ModernGumpsOff = true;

        // A world map or party gump a run saved open covers the world the
        // walk checks hold on; the checks start without them.
        UIManager.GetGump<WorldMapGump>()?.Dispose();
        UIManager.GetGump<PartyGump>()?.Dispose();
        await Frames(host, 5);

        await WalkCheck(host, world);
        await DoubleTapCheck(host, world);
        await PinchCheck(host);
        await BarCheck(host);
        await ParkCheck(host);
        await TargetTapCheck(host, world);
        await CommandBarCheck(host, world);
        await BarHoldCheck(host, world);
        await FlickCheck(host, world);
        // The classic Options on touch, as a player who turned Modern off has it.
        await OptionsTouchCheck(host, world);
        await MobileOptionsCheck(host, world);
        Configuration.ProfileManager.CurrentProfile.ModernGumpsOff = false;
        await ModernOptionsCheck(host, world);
        await ModernPartyCheck(host, world);
        await ModernSkillsCheck(host, world);
        await ModernSpellbookCheck(host, world);
        Configuration.ProfileManager.CurrentProfile.ModernGumpsOff = true;
        await HelpGumpCheck(host, world);
        await WorldMapCheck(host, world);
        await PaperdollFitCheck(host, world);
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

        // Diagonals first, then straight: a character left on a coast by an
        // earlier run has water on some sides.
        foreach (Vector2 diagonal in new[] { new Vector2(1, 1), new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1), new Vector2(1, 0) })
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

        // C8: a bar button runs when the finger lifts, not when it lands.
        // Down on Journal: nothing yet. Slid well off it: let go, and nothing
        // runs on lifting. Rolled a few pixels: it still runs.
        UIManager.GetGump<JournalGump>()?.Dispose();
        await Frames(host, 5);
        TouchInput.Trace.Clear();
        Rect2 journal = bar.ButtonRect("journal");
        Vector2 at = journal.Position + journal.Size / 2;

        Touch(0, at, true);
        await Frames(host, 5);
        bool notOnDown = UIManager.GetGump<JournalGump>() == null;
        Drag(0, at + new Vector2(0, -TouchInput.BarSlopPixels * 3), new Vector2(0, -TouchInput.BarSlopPixels * 3));
        await Frames(host, 2);
        Touch(0, at + new Vector2(0, -TouchInput.BarSlopPixels * 3), false);
        await Frames(host, 30);

        Check(
            "a bar button does nothing on the finger's landing, and nothing if the finger slides off it",
            notOnDown && UIManager.GetGump<JournalGump>() == null && TouchInput.Trace.Exists(t => t.StartsWith("bar -> journal let go")),
            string.Join(" | ", TouchInput.Trace)
        );

        TouchInput.Trace.Clear();
        Vector2 rolled = at + new Vector2(TouchInput.BarSlopPixels / 2, 0);
        Touch(0, at, true);
        await Frames(host, 3);
        Drag(0, rolled, rolled - at);
        await Frames(host, 2);
        Touch(0, rolled, false);
        await Frames(host, 30);

        Check(
            "a finger that rolls a little on a bar button still presses it on lifting",
            UIManager.GetGump<JournalGump>() != null && TouchInput.Trace.Contains("bar -> journal"),
            string.Join(" | ", TouchInput.Trace)
        );
        UIManager.GetGump<JournalGump>()?.Dispose();
        await Frames(host, 5);
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
            + $"frame process {process / frames * 1000:F2} ms, {UIManager.Gumps.Count} gumps, rows {bar.RowsOpen}";
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
    /// The command bar (C8): row 1 never moves; War mode opens two rows; a
    /// row button runs its macro; the handle's tap goes 1 to 2 and back to 1;
    /// a closed bar stays closed through the next War mode; a slow drag snaps
    /// to the nearest row count and a flick goes all the way; a drag that
    /// starts on a row runs nothing and moves nothing; the targeting swap
    /// happens in place. Leaves three rows up for the photograph.
    /// </summary>
    private static async System.Threading.Tasks.Task CommandBarCheck(Node host, Game.World world)
    {
        TouchGumpBar bar = TouchInput.Bar;
        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;

        if (bar == null || profile == null)
        {
            Check("the command bar", false, "no bar or no profile");

            return;
        }

        // A desktop profile does not have the mobile defaults; the probe
        // turns them on for this run, as Options would.
        profile.TouchMacroRow = true;
        profile.TouchBarSlots = TouchGumpBar.DefaultSlots;
        profile.TouchReduceMotion = false;

        if (world.Player.InWarMode)
        {
            Game.GameActions.ToggleWarMode(world.Player);
            await Frames(host, 60);
        }

        // A character that logged in at war has already had its rows open.
        bar.ResetSession();
        await Frames(host, 5);
        Check("the handle is shown and one row is open", bar.HandleShown && bar.RowsOpen == 1 && bar.Height == 1f);

        Rect2 row1 = bar.SlotRect(1, 0);
        Rect2 handle1 = bar.HandleRect();
        TouchInput.Trace.Clear();
        Game.GameActions.ToggleWarMode(world.Player);
        await Settled(host, bar);

        Check(
            "War mode opens two rows, row 1 stays where it was and the handle rises one row",
            world.Player.InWarMode && bar.RowsOpen == 2 && bar.SlotRect(1, 0) == row1
                && Mathf.IsEqualApprox(handle1.Position.Y - bar.HandleRect().Position.Y, row1.Size.Y),
            $"war {world.Player.InWarMode}, rows {bar.RowsOpen}, row 1 {row1} -> {bar.SlotRect(1, 0)}, handle {handle1.Position.Y} -> {bar.HandleRect().Position.Y} | {string.Join(" | ", TouchInput.Trace)}"
        );

        Rect2 war = bar.ButtonRect("war");
        await Tap(host, war.GetCenter());
        await Frames(host, 60);

        Check("the War/Peace button ran its macro", !world.Player.InWarMode, string.Join(" | ", TouchInput.Trace));

        Check("row 2 holds the proposal's buttons",
            string.Join(",", TouchGumpBar.Row(2)) == "next,object,heal,cure,ability1,ability2,lastspell,lastskill,armdisarm,status");

        await Tap(host, bar.HandleRect().GetCenter());
        await Settled(host, bar);

        Check("a tap on the handle at two rows closes to one", bar.RowsOpen == 1 && bar.HiddenThisSession,
            string.Join(" | ", TouchInput.Trace));

        Game.GameActions.ToggleWarMode(world.Player);
        await Settled(host, bar);

        Check("a closed bar stays at one row on entering War mode", world.Player.InWarMode && bar.RowsOpen == 1);

        Game.GameActions.ToggleWarMode(world.Player);
        await Frames(host, 60);

        await Tap(host, bar.HandleRect().GetCenter());
        await Settled(host, bar);
        Check("a tap on the handle at one row opens two", bar.RowsOpen == 2, string.Join(" | ", TouchInput.Trace));

        await Tap(host, bar.HandleRect().GetCenter());
        await Settled(host, bar);

        // A slow drag of a row and a bit snaps to the nearest count: two.
        TouchInput.Trace.Clear();
        Vector2 grip = bar.HandleRect().GetCenter();
        await Swipe(host, grip, grip - new Vector2(0, row1.Size.Y * 1.3f), 700);
        await Settled(host, bar);

        Check("a slow drag on the handle snaps to the nearest row count", bar.RowsOpen == 2,
            $"rows {bar.RowsOpen} | {string.Join(" | ", TouchInput.Trace)}");

        // A flick up goes all the way; a flick down all the way back.
        TouchInput.Trace.Clear();
        grip = bar.HandleRect().GetCenter();
        await Swipe(host, grip, grip - new Vector2(0, 60), 40);
        await Settled(host, bar);
        bool up = bar.RowsOpen == 3;

        grip = bar.HandleRect().GetCenter();
        await Swipe(host, grip, grip + new Vector2(0, 60), 40);
        await Settled(host, bar);

        Check("a flick up opens three rows and a flick down closes to one", up && bar.RowsOpen == 1,
            $"up {up}, rows {bar.RowsOpen} | {string.Join(" | ", TouchInput.Trace)}");

        // The rows never take the gesture: a drag from Map moves nothing and opens nothing.
        UIManager.GetGump<MiniMapGump>()?.Dispose();
        await Frames(host, 5);
        TouchInput.Trace.Clear();
        Rect2 map = bar.ButtonRect("map");
        await Swipe(host, map.GetCenter(), map.GetCenter() - new Vector2(0, 200), 500);
        await Frames(host, 30);

        Check("a drag that starts on a row button moves no rows and runs nothing",
            bar.RowsOpen == 1 && UIManager.GetGump<MiniMapGump>() == null,
            $"rows {bar.RowsOpen} | {string.Join(" | ", TouchInput.Trace)}");

        // While targeting, Chat and War/Peace become Self and Cancel in place.
        int chatAt = System.Array.IndexOf(TouchGumpBar.Row(1), "chat");
        int warAt = System.Array.IndexOf(TouchGumpBar.Row(1), "war");
        world.TargetManager.SetTargeting(CursorTarget.Position, 0, TargetType.Neutral);
        await Frames(host, 5);
        string[] swapped = TouchGumpBar.Row(1);
        bool inPlace = swapped[chatAt] == "self" && swapped[warAt] == "cancel";
        await Tap(host, bar.SlotRect(1, warAt).GetCenter());
        await Frames(host, 10);

        Check("while targeting, Chat and War/Peace are Self and Cancel in place, and Cancel cancels",
            inPlace && !world.TargetManager.IsTargeting, string.Join(",", swapped));

        grip = bar.HandleRect().GetCenter();
        await Swipe(host, grip, grip - new Vector2(0, 60), 40);
        await Settled(host, bar);

        // For the photograph: the nearest other mobile as the last target,
        // so the handle strip shows its name and strips.
        Game.GameObjects.Mobile nearest = null;

        foreach (Game.GameObjects.Mobile m in world.Mobiles.Values)
        {
            if (m != world.Player && (nearest == null || m.Distance < nearest.Distance))
            {
                nearest = m;
            }
        }

        if (nearest != null)
        {
            world.TargetManager.LastTargetInfo.SetEntity(nearest.Serial);
        }
    }

    /// <summary>
    /// The hold popup (C10): a hold on a bar button opens three buttons above
    /// it; sliding onto an alternate and letting go runs it; sliding onto Edit
    /// opens the slot editor, where a pick and Save change the slot; letting go
    /// anywhere else runs nothing. Then the rule for tall windows: one moved up
    /// to fit above the open rows, and only one taller than that room left
    /// overlapping them.
    /// </summary>
    private static async System.Threading.Tasks.Task BarHoldCheck(Node host, Game.World world)
    {
        TouchGumpBar bar = TouchInput.Bar;
        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;

        if (bar == null || profile == null)
        {
            Check("the hold popup", false, "no bar or no profile");
            return;
        }

        profile.TouchBarSlots = TouchGumpBar.DefaultSlots;
        profile.TouchBarAlts = null;
        int slot = System.Array.IndexOf(TouchGumpBar.Row(1), "attack");
        Check("a slot with alternates is marked, and Attack Last's first is Attack Selected",
            TouchGumpBar.HasAlternates(slot) && TouchGumpBar.Alternates(slot).alt1 == "attacksel");

        // Hold, slide to the first alternate, let go.
        TouchInput.Trace.Clear();
        Rect2 cell = bar.SlotRect(1, slot);
        await HoldThenSlide(host, bar, cell.GetCenter(), 1);

        Check("hold, slide onto an alternate and let go: it runs, and the tap's action does not",
            TouchInput.Trace.Contains("popup -> attacksel") && !TouchInput.Trace.Contains("bar -> attack"),
            string.Join(" | ", TouchInput.Trace));

        // Hold and let go off the popup: nothing.
        TouchInput.Trace.Clear();
        await HoldThenSlide(host, bar, cell.GetCenter(), 0);

        Check("hold, then let go away from the popup: nothing runs",
            TouchInput.Trace.Contains("popup -> cancelled") && !TouchInput.Trace.Exists(t => t.StartsWith("bar -> attack")),
            string.Join(" | ", TouchInput.Trace));

        // Hold, slide to Edit: the editor, for this slot.
        TouchInput.Trace.Clear();
        await HoldThenSlide(host, bar, cell.GetCenter(), 3);
        await Frames(host, 10);

        Check("hold, slide onto Edit: the slot editor opens for that slot",
            BarEditor.IsOpen && BarEditor.Slot == slot, string.Join(" | ", TouchInput.Trace));

        if (BarEditor.IsOpen)
        {
            // Change the second alternate to Next Hostile, with taps, and save.
            await TapClient(host, BarEditor.CentreOf("Hold 2: Ability 1"));
            await TapClient(host, BarEditor.CentreOf("Targeting"));
            await TapClient(host, BarEditor.CentreOf("nexthostile"));
            string[] picked = BarEditor.Picked;
            await TapClient(host, BarEditor.CentreOf("Save"));
            await Frames(host, 5);

            Check("in the editor, a pick and Save change the slot's second alternate, by name",
                !BarEditor.IsOpen && TouchGumpBar.Alternates(slot).alt2 == "nexthostile" && TouchGumpBar.Slots[slot] == "attack",
                $"picked {string.Join(",", picked ?? new string[0])}, now {TouchGumpBar.Alternates(slot)}, alts \"{profile.TouchBarAlts}\"");
        }

        profile.TouchBarAlts = null;

        // Tall windows and three open rows.
        Game.GameActions.OpenSettings(world);
        await Frames(host, 30);
        TouchGumpBar.Row(1);
        Game.UI.Gumps.Gump options = UIManager.GetGump<Game.UI.Gumps.OptionsGump>();
        Vector2 grip = bar.HandleRect().GetCenter();
        await Swipe(host, grip, grip - new Vector2(0, 400), 60);
        await Settled(host, bar);
        await Frames(host, 40);

        if (options != null)
        {
            int room = GumpPresentation.DisplayBounds(false).Height;
            int h = GumpPresentation.Height(options);
            bool fits = h <= room ? options.Y + h <= room : options.Y == 0;
            Check("with three rows open, a tall window moves up to fit above them, or to the top if taller than the room",
                bar.RowsOpen == 3 && fits, $"rows {bar.RowsOpen}, room {room}, options at {options.Y} height {h}");
            options.Dispose();
        }

        await Frames(host, 5);
    }

    /// <summary>
    /// A finger held on a bar button until its popup opens, then slid to popup
    /// button <paramref name="index"/> (1, 2 the alternates, 3 Edit; 0 away
    /// from it) and lifted.
    /// </summary>
    private static async System.Threading.Tasks.Task HoldThenSlide(Node host, TouchGumpBar bar, Vector2 at, int index)
    {
        Touch(0, at, true);
        ulong until = Godot.Time.GetTicksMsec() + TouchInput.BarPopupMs + 150;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }

        Vector2 to = index > 0 ? bar.PopupRect(index).GetCenter() : new Vector2(at.X, 40);
        Vector2 mid = (at + to) / 2;
        Drag(0, mid, mid - at);
        await Frames(host, 2);
        Drag(0, to, to - mid);
        await Frames(host, 2);
        Touch(0, to, false);
        await Frames(host, 10);
    }

    /// <summary>A tap at a point in client pixels (a card's control), as a finger.</summary>
    private static async System.Threading.Tasks.Task TapClient(Node host, Vector2? client)
    {
        if (client == null)
        {
            return;
        }

        await Tap(host, client.Value * GUO.Client.Game.DpiScale);
        await Frames(host, 3);
    }

    /// <summary>Wait for the bar to stop moving (its settle is 200 ms).</summary>
    private static async System.Threading.Tasks.Task Settled(Node host, TouchGumpBar bar)
    {
        ulong until = Godot.Time.GetTicksMsec() + 1500;
        await Frames(host, 3);

        while (bar.Moving && Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }

        await Frames(host, 3);
    }

    /// <summary>A finger from one point to another over a time, whatever the frame rate.</summary>
    private static async System.Threading.Tasks.Task Swipe(Node host, Vector2 from, Vector2 to, int milliseconds)
    {
        Touch(0, from, true);
        await Frames(host, 1);
        ulong start = Godot.Time.GetTicksMsec();
        Vector2 last = from;

        while (true)
        {
            float t = System.Math.Min(1f, (Godot.Time.GetTicksMsec() - start) / (float)milliseconds);
            Vector2 at = from.Lerp(to, t);
            Drag(0, at, at - last);
            last = at;

            if (t >= 1f)
            {
                break;
            }

            await Frames(host, 1);
        }

        Touch(0, to, false);
        await Frames(host, 5);
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
    /// <summary>
    /// Options on touch (C11): it opens fitted to the screen, over the command
    /// bar (which steps aside), with its button row on screen; a tap toggles a
    /// check box; a combo box's list opens where the box is drawn, at its
    /// scale; a page tab switches the page; Okay closes it and the bar is back.
    /// </summary>
    private static async System.Threading.Tasks.Task MobileOptionsCheck(Node host, Game.World world)
    {
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await Frames(host, 5);
        Game.GameActions.OpenSettings(world);
        await Frames(host, 30);
        OptionsGump options = UIManager.GetGump<OptionsGump>();
        TouchGumpBar bar = TouchInput.Bar;

        if (options == null || bar == null)
        {
            Check("mobile Options", false, "no Options or no bar");
            return;
        }

        Compat.Rectangle screen = GUO.Client.Game.ClientBounds;
        Compat.Rectangle drawn = GumpPresentation.Bounds(options);
        Check("Options opens fitted to the screen, over the command bar, its button row on screen",
            options.PresentationScale > 1.2f && bar.Covered && bar.ReservedFraction == 0f
                && drawn.Bottom <= screen.Height - 8 && drawn.Right <= screen.Width
                && drawn.Y >= GumpPresentation.FullHeightTop() + 8,
            $"scale {options.PresentationScale:0.00}, at {drawn}, screen {screen.Width}x{screen.Height}, bar covered {bar.Covered}");

        // A tap on the first check box in view toggles it; a second puts it back.
        Game.UI.Controls.Checkbox box = First<Game.UI.Controls.Checkbox>(options);
        bool toggled = false;

        if (box != null)
        {
            bool was = box.IsChecked;
            await Tap(host, DrawnCentre(box));
            await Frames(host, 5);
            toggled = box.IsChecked != was;
            await Tap(host, DrawnCentre(box));
            await Frames(host, 5);
            toggled &= box.IsChecked == was;
        }

        Check("a tap on a check box in the fitted Options toggles it", toggled, box == null ? "no check box" : box.Text);

        // A combo box: its list opens at the box, drawn at the same scale.
        Game.UI.Controls.Combobox combo = First<Game.UI.Controls.Combobox>(options);
        bool listOk = false;
        string listDetail = "no combo box";

        if (combo != null)
        {
            Vector2 at = DrawnCentre(combo);
            await Tap(host, at);
            await Frames(host, 10);
            Gump list = null;

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.GetType().Name == "ComboboxGump" && !g.IsDisposed) list = g;
            }

            Compat.Point boxAt = GumpPresentation.ToScreen(combo, new Compat.Point(combo.ScreenCoordinateX, combo.ScreenCoordinateY));
            listOk = list != null && System.Math.Abs(list.X - boxAt.X) <= 2 && Mathf.IsEqualApprox(list.PresentationScale, options.PresentationScale);
            listDetail = list == null ? "no list" : $"list at {list.X},{list.Y} x{list.PresentationScale:0.00}, box at {boxAt.X},{boxAt.Y}";
            list?.Dispose();
            await Frames(host, 5);
        }

        Check("a combo box in the fitted Options opens its list at the box, at its scale", listOk, listDetail);

        // The Sound page's tab.
        Game.UI.Controls.NiceButton sound = null;

        foreach (Game.UI.Controls.Control c in options.Children)
        {
            if (c is Game.UI.Controls.NiceButton nb && nb.ButtonParameter == 2 && nb.X == 10) sound = nb; // the page column: Sound is page 2
        }

        if (sound != null)
        {
            await Tap(host, DrawnCentre(sound));
            await Frames(host, 5);
        }

        Check("a tap on a page tab in the fitted Options switches the page", options.ActivePage == 2, $"page {options.ActivePage}");

        // A slider on that page follows the finger across the scaled gump.
        Game.UI.Controls.HSliderBar slider = First<Game.UI.Controls.HSliderBar>(options);
        string sliderDetail = "no slider";
        bool slid = false;

        if (slider != null)
        {
            int kept = slider.Value;
            // From the thumb's end to the bar's middle: about half.
            int y = slider.ScreenCoordinateY + slider.Height / 2;
            Compat.Point from = GumpPresentation.ToScreen(slider, new Compat.Point(slider.ScreenCoordinateX + slider.Width - 4, y));
            Compat.Point mid = GumpPresentation.ToScreen(slider, new Compat.Point(slider.ScreenCoordinateX + slider.Width / 2, y));
            await Swipe(host, Client(new Vector2(from.X, from.Y)), Client(new Vector2(mid.X, mid.Y)), 400);
            await Frames(host, 5);
            int half = (slider.MinValue + slider.MaxValue) / 2, slack = (slider.MaxValue - slider.MinValue) / 8;
            slid = System.Math.Abs(slider.Value - half) <= slack;
            sliderDetail = $"{kept} -> {slider.Value} of {slider.MinValue}..{slider.MaxValue}";
            slider.Value = kept;
        }

        Check("a slider in the fitted Options follows the finger (dragged to the middle: about half)", slid, sliderDetail);

        // Okay: closed, and the bar is back.
        Game.UI.Controls.Button ok = null;

        foreach (Game.UI.Controls.Control c in options.Children)
        {
            if (c is Game.UI.Controls.Button b && b.ButtonID == 4) ok = b;
        }

        if (ok != null)
        {
            await Tap(host, DrawnCentre(ok));
            await Frames(host, 20);
        }

        Check("Okay closes the fitted Options and the command bar comes back",
            (options.IsDisposed || UIManager.GetGump<OptionsGump>() == null) && !bar.Covered && bar.ReservedFraction > 0f,
            $"disposed {options.IsDisposed}, covered {bar.Covered}");
    }

    /// <summary>
    /// The shard's help gump (ModernUO's HelpGump, known by its type ID) opens
    /// full-height: fitted below the top bar, over the command bar.
    /// </summary>
    private static async System.Threading.Tasks.Task HelpGumpCheck(Node host, Game.World world)
    {
        TouchGumpBar bar = TouchInput.Bar;
        Game.GameActions.RequestHelp();
        Gump help = null;

        for (int i = 0; i < 180 && help == null; i++)
        {
            await Frames(host, 1);

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsFromServer && !g.IsDisposed && g.ServerSerial == 0x7510FA8F) help = g;
            }
        }

        await Frames(host, 10);
        Check("the shard's help gump (ModernUO HelpGump, by type ID) opens full-height and fitted",
            help != null && bar.Covered && help.PresentationScale > 1.2f && help.Y >= GumpPresentation.FullHeightTop(),
            help == null ? "no help gump" : $"x{help.PresentationScale:0.00} at {GumpPresentation.Bounds(help)}");
        help?.Dispose();
        await Frames(host, 10);
    }

    /// <summary>
    /// Modern Options (ADR-0024): on touch, opening Options opens its Modern
    /// view (the classic gump is not added), over the command bar; a page tab
    /// switches the page; a check box row and a slider change their values;
    /// Okay writes them to the profile fields the classic Apply writes; Cancel
    /// writes nothing; Classic view opens the ported Options.
    /// </summary>
    private static async System.Threading.Tasks.Task ModernOptionsCheck(Node host, Game.World world)
    {
        Configuration.Profile p = Configuration.ProfileManager.CurrentProfile;
        TouchGumpBar bar = TouchInput.Bar;
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await Frames(host, 5);

        Game.GameActions.OpenSettings(world);
        await Frames(host, 20);
        var view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernOptions;
        Check("on touch, Options opens as its Modern view, over the command bar, the classic gump not added",
            view != null && Input.Touch.Modern.ModernGump.IsOpen && UIManager.GetGump<OptionsGump>() == null && bar.Covered,
            $"modern {view != null}, classic {UIManager.GetGump<OptionsGump>() != null}, covered {bar.Covered}");

        if (view == null)
        {
            return;
        }

        // General: toggle Always run by a tap on its row; Sound: drag the music volume to the middle.
        bool run = p.AlwaysRun;
        await TapClient(host, view.CentreOf(view.Find("Always run")));
        await TapClient(host, view.CentreOf(view.Find("Sound")));
        Godot.Control music = view.Find("Music volume");
        int musicWas = p.MusicVolume;

        if (music != null)
        {
            await Swipe(host, Client(view.AlongOf(music, 0.95f)), Client(view.AlongOf(music, 0.5f)), 400);
            await Frames(host, 5);
        }

        await TapClient(host, view.CentreOf(view.Find("Okay")));
        await Frames(host, 10);

        Check("in Modern Options, a tapped check box and a dragged slider land in the profile on Okay",
            !Input.Touch.Modern.ModernGump.IsOpen && p.AlwaysRun != run && System.Math.Abs(p.MusicVolume - 50) <= 10 && !bar.Covered,
            $"always run {run} -> {p.AlwaysRun}, music {musicWas} -> {p.MusicVolume}, open {Input.Touch.Modern.ModernGump.IsOpen}");

        // Cancel writes nothing.
        Game.GameActions.OpenSettings(world);
        await Frames(host, 10);
        view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernOptions;
        bool before = p.AlwaysRun;
        await TapClient(host, view.CentreOf(view.Find("General")));
        await TapClient(host, view.CentreOf(view.Find("Always run")));
        await TapClient(host, view.CentreOf(view.Find("Cancel")));
        await Frames(host, 5);
        Check("Cancel in Modern Options writes nothing", p.AlwaysRun == before && !Input.Touch.Modern.ModernGump.IsOpen);

        // Put them back, and Classic view opens the ported gump.
        p.AlwaysRun = run;
        p.MusicVolume = musicWas;
        Game.GameActions.OpenSettings(world);
        await Frames(host, 10);
        view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernOptions;
        await TapClient(host, view.CentreOf(view.Find("Classic view")));
        await Frames(host, 20);
        Check("Classic view opens the ported Options, fitted", UIManager.GetGump<OptionsGump>() is OptionsGump classic
            && classic.PresentationScale > 1.2f && !Input.Touch.Modern.ModernGump.IsOpen);
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await Frames(host, 10);
    }

    /// <summary>
    /// Modern Party (ADR-0024, gump 2): opening the party gump opens its
    /// Modern view; out of a party it says so; Add member sends the classic
    /// invite request (the shard answers with a target cursor); Cancel closes.
    /// </summary>
    private static async System.Threading.Tasks.Task ModernPartyCheck(Node host, Game.World world)
    {
        UIManager.Add(new PartyGump(world, 100, 100, world.Party.CanLoot));
        await Frames(host, 20);
        var view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernParty;
        Check("the party gump opens as its Modern view, the classic gump not added",
            view != null && UIManager.GetGump<PartyGump>() == null && TouchInput.Bar.Covered,
            $"modern {view != null}, classic {UIManager.GetGump<PartyGump>() != null}");

        if (view == null)
        {
            return;
        }

        await TapClient(host, view.CentreOf(view.Find("Add member")));
        bool targeting = false;

        for (int i = 0; i < 120 && !targeting; i++)
        {
            await Frames(host, 1);
            targeting = world.TargetManager.IsTargeting;
        }

        Check("Modern Party's Add member sends the invite request (the shard's target cursor comes up)", targeting,
            world.Party.Leader == 0 ? "not in a party (as expected)" : "in a party");
        world.TargetManager.CancelTarget();
        await Frames(host, 5);

        if (Input.Touch.Modern.ModernGump.IsOpen)
        {
            await TapClient(host, view.CentreOf(view.Find("Cancel")));
            await Frames(host, 5);
        }

        Check("Cancel closes Modern Party", !Input.Touch.Modern.ModernGump.IsOpen);
    }

    /// <summary>
    /// Modern Skills (ADR-0024, gump 3): opening the skills opens its Modern
    /// view with every skill; a tap on a lock cycles it through the classic
    /// states; the group stepper narrows the list; Use uses the skill and closes.
    /// </summary>
    private static async System.Threading.Tasks.Task ModernSkillsCheck(Node host, Game.World world)
    {
        Game.GameActions.OpenSkills(world);
        await Frames(host, 20);
        var view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernSkills;
        int all = world.Player.Skills.Length;
        Check("the skills gump opens as its Modern view, every skill listed, the classic not added",
            view != null && UIManager.GetGump<StandardSkillsGump>() == null && UIManager.GetGump<SkillGumpAdvanced>() == null
                && view.Find("lock Hiding") != null,
            $"modern {view != null}, skills {all}");

        if (view == null)
        {
            return;
        }

        Game.Data.Skill hiding = System.Array.Find(world.Player.Skills, s => s?.Name == "Hiding");
        Game.Data.Lock was = hiding.Lock;
        var seen = new System.Collections.Generic.List<Game.Data.Lock>();

        for (int i = 0; i < 3; i++)
        {
            await TapClient(host, view.CentreOf(view.Find("lock Hiding")));
            await Frames(host, 5);
            seen.Add(hiding.Lock);
        }

        Check("a tap on a skill's lock cycles it through the classic states and back",
            seen.Count == 3 && seen[2] == was && seen[0] != was && seen[1] != was && seen[0] != seen[1],
            $"{was} -> {string.Join(" -> ", seen)}");

        await TapClient(host, view.CentreOf(view.Find("group next")));
        await Frames(host, 5);
        bool narrowed = view.Find("lock Hiding") == null || world.SkillsGroupManager.Groups.Count == 0;
        await TapClient(host, view.CentreOf(view.Find("group prev")));
        await Frames(host, 5);
        Check("the group stepper narrows the list", narrowed && view.Find("lock Hiding") != null);

        await TapClient(host, view.CentreOf(view.Find("use Hiding")));
        await Frames(host, 10);
        Check("Use on a skill uses it and closes Modern Skills", !Input.Touch.Modern.ModernGump.IsOpen);
    }

    /// <summary>
    /// Modern Spellbook (ADR-0024, gump 6): a spellbook in the pack, opened as a
    /// player opens it (a double-click; the shard sends the book), shows as the
    /// Modern grid; a hold on a spell places its UseSpellButtonGump; a tap
    /// casts (a cursor or the words follow). Skipped without a book.
    /// </summary>
    private static async System.Threading.Tasks.Task ModernSpellbookCheck(Node host, Game.World world)
    {
        Game.GameObjects.Item pack = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
        Game.GameObjects.Item book = null;

        for (var i = pack?.Items; i != null; i = i.Next)
        {
            if (i is Game.GameObjects.Item it && it.Graphic == 0x0EFA) book = it;
        }

        bool standIn = book == null;

        if (standIn)
        {
            // No book in the pack (a shard that gave none, and no GM to add
            // one): a stand-in in the client's world, as the shard would send
            // it: a Magery book whose children's amounts are spell indices.
            book = world.GetOrCreateItem(0x7FFFFF00);
            book.Graphic = 0x0EFA;

            // In the pack: an item on no container counts as on the ground far
            // away, and the world's range cleanup takes it.
            book.Container = pack.Serial;
            pack.PushToBack(book);

            for (int i = 1; i <= 16; i++)
            {
                Game.GameObjects.Item spell = world.GetOrCreateItem(0x7FFFFF00 + (uint)i);
                spell.Amount = (ushort)i;
                spell.Container = book.Serial;
                book.PushToBack(spell);
            }

            UIManager.Add(new SpellbookGump(world, book.Serial));
        }
        else
        {
            Game.GameActions.DoubleClick(world, book.Serial);
        }
        Input.Touch.Modern.ModernSpellbook view = null;

        for (int i = 0; i < 180 && view == null; i++)
        {
            await Frames(host, 1);
            view = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernSpellbook;
        }

        await Frames(host, 20);
        Godot.Control tile = view?.Find("Heal") ?? view?.Find("Clumsy");
        Check("a spellbook opens as its Modern grid, the classic book not added",
            view != null && UIManager.GetGump<SpellbookGump>() == null && tile != null,
            $"modern {view != null}, classic {UIManager.GetGump<SpellbookGump>() != null}, a tile {tile != null}");

        if (view == null || tile == null)
        {
            Input.Touch.Modern.ModernGump.Current?.Close();

            if (standIn)
            {
                world.RemoveItem(book.Serial, true);
            }

            return;
        }

        // Hold: the spell's button, the desktop's UseSpellButtonGump.
        Vector2 at = Client(view.CentreOf(tile));
        Touch(0, at, true);
        ulong until = Godot.Time.GetTicksMsec() + 700;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await Frames(host, 1);
        }

        Touch(0, at, false);
        await Frames(host, 10);
        UseSpellButtonGump button = UIManager.GetGump<UseSpellButtonGump>();
        Check("a hold on a spell places its spell button (UseSpellButtonGump) and closes the book",
            button != null && !Input.Touch.Modern.ModernGump.IsOpen, button == null ? "no button" : $"spell {button.SpellID}");
        button?.Dispose();
        await Frames(host, 5);

        if (standIn)
        {
            world.RemoveItem(book.Serial, true);
        }
    }

    /// <summary>
    /// The world map (Classic + fit + gestures): opened during play it is sized
    /// to the room below the top bar, over the command bar, at its own zoom;
    /// a pinch on it zooms the map, not the gump.
    /// </summary>
    private static async System.Threading.Tasks.Task WorldMapCheck(Node host, Game.World world)
    {
        TouchGumpBar bar = TouchInput.Bar;
        Compat.Rectangle screen = GUO.Client.Game.ClientBounds;
        int top = GumpPresentation.FullHeightTop();
        Game.GameActions.OpenWorldMap(world);
        await Frames(host, 60);
        WorldMapGump map = UIManager.GetGump<WorldMapGump>();
        Check("the world map opens full-height: sized (not scaled) to the room below the top bar, over the command bar",
            map != null && bar.Covered && map.Y >= top && map.Height >= screen.Height - top - 30 && map.PresentationScale == 1f,
            map == null ? "no map" : $"at {map.X},{map.Y} {map.Width}x{map.Height}, top {top}, covered {bar.Covered}");

        if (map != null)
        {
            float zoom = map.Zoom;
            Vector2 c = Client(new Vector2(map.X + map.Width / 2f, map.Y + map.Height / 2f));
            // Fingers together, then apart if that changed nothing: a saved
            // zoom may already be the farthest out or in.
            for (int attempt = 0; attempt < 2 && map.Zoom == zoom; attempt++)
            {
                float from = attempt == 0 ? 220 : 40, to = attempt == 0 ? 40 : 220;
                Touch(0, c - new Vector2(from, 0), true);
                Touch(1, c + new Vector2(from, 0), true);
                await Frames(host, 2);

                for (int i = 1; i <= 12; i++)
                {
                    float d = from + (to - from) * i / 12f;
                    Drag(0, c - new Vector2(d, 0), new Vector2((to - from) / -12f, 0));
                    Drag(1, c + new Vector2(d, 0), new Vector2((to - from) / 12f, 0));
                    await Frames(host, 2);
                }

                Touch(0, c - new Vector2(to, 0), false);
                Touch(1, c + new Vector2(to, 0), false);
                await Frames(host, 10);
            }

            Check("a pinch on the world map zooms the map, not the gump", map.Zoom != zoom && map.PresentationScale == 1f,
                $"zoom {zoom} -> {map.Zoom}");

            // Its markers manager, Modern (gump index 9): over the map; Go to
            // centres the map on a marker (free view), where files are loaded.
            Configuration.ProfileManager.CurrentProfile.ModernGumpsOff = false;
            UIManager.Add(new MarkersManagerGump(world));
            await Frames(host, 20);
            var markers = Input.Touch.Modern.ModernGump.Current as Input.Touch.Modern.ModernMarkers;
            Check("the map's markers manager opens as its Modern view", markers != null && UIManager.GetGump<MarkersManagerGump>() == null);

            if (markers != null && markers.FirstMarker is string first && markers.Find("go " + first) is Godot.Control go)
            {
                await TapClient(host, markers.CentreOf(go));
                await Frames(host, 10);
                Check("Go to on a marker centres the world map on it and closes the list", map.FreeView && !Input.Touch.Modern.ModernGump.IsOpen, first);
            }
            else
            {
                GD.Print("[GUO] touch probe: no marker files loaded; Go to is not checked");
                Input.Touch.Modern.ModernGump.Current?.Close();
            }

            Configuration.ProfileManager.CurrentProfile.ModernGumpsOff = true;
            map.FreeView = false;
            map.Dispose();
            await Frames(host, 10);
        }
    }

    /// <summary>
    /// The paperdoll's touch fit: one opened during play on one screen starts
    /// larger than 1x (up to 2x, within the room); a size the player gives one
    /// is the size the next opens at.
    /// </summary>
    private static async System.Threading.Tasks.Task PaperdollFitCheck(Node host, Game.World world)
    {
        // Earlier checks resize paperdolls, which the fit remembers; this one
        // starts as a fresh session does.
        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        await Frames(host, 5);
        GumpPresentation.PaperdollScale = 0f;
        Game.GameActions.OpenPaperdoll(world, world.Player.Serial);
        PaperDollGump doll = null;

        for (int i = 0; i < 120 && doll == null; i++)
        {
            await Frames(host, 1);
            doll = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        }

        await Frames(host, 10);
        float first = doll?.PresentationScale ?? 0f;
        bool fits = doll != null && GumpPresentation.Bounds(doll).Height <= GumpPresentation.DisplayBounds(false).Height;

        if (doll != null)
        {
            GumpPresentation.SetScale(doll, 1.5f, new Compat.Point(doll.X, doll.Y));
            await Frames(host, 5);
            doll.Dispose();
            await Frames(host, 5);
        }

        Game.GameActions.OpenPaperdoll(world, world.Player.Serial);
        PaperDollGump again = null;

        for (int i = 0; i < 120 && again == null; i++)
        {
            await Frames(host, 1);
            again = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        }

        await Frames(host, 10);
        Check("a paperdoll opened on one touch screen starts larger than 1x, within the room; the next opens at the size the player gave the last",
            first > 1.3f && fits && again != null && Mathf.IsEqualApprox(again.PresentationScale, 1.5f),
            $"first {first:0.00}, fits {fits}, next {again?.PresentationScale:0.00}");

        // The long press after this looks for its point at 1x.
        if (again != null)
        {
            GumpPresentation.Reset(again);
            await Frames(host, 5);
        }
    }

    /// <summary>The first control of a type on the gump's open page, drawn inside its scroll area.</summary>
    private static T First<T>(Gump g) where T : Game.UI.Controls.Control
    {
        foreach (Game.UI.Controls.Control top in g.Children)
        {
            if (!top.IsVisible || top.Page != 0 && top.Page != g.ActivePage) continue;

            T found = Find<T>(top);
            if (found != null) return found;
        }

        return null;

        static T Find<TC>(Game.UI.Controls.Control c) where TC : Game.UI.Controls.Control
        {
            if (c is T t && c.IsVisible && c.Width > 0) return t;

            foreach (Game.UI.Controls.Control child in c.Children)
            {
                if (!child.IsVisible) continue;
                T f = Find<TC>(child);
                if (f != null) return f;
            }

            return null;
        }
    }

    /// <summary>The window position of a control's centre, where its gump draws it.</summary>
    private static Vector2 DrawnCentre(Game.UI.Controls.Control c)
    {
        Compat.Point p = GumpPresentation.ToScreen(c, new Compat.Point(c.ScreenCoordinateX + c.Width / 2, c.ScreenCoordinateY + c.Height / 2));
        return Client(new Vector2(p.X, p.Y));
    }

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
            // Where the area is drawn: Options is fitted to the screen on touch (C11).
            Compat.Point drawn = GumpPresentation.ToScreen(area,
                new Compat.Point(area.ScreenCoordinateX + 60, area.ScreenCoordinateY + (int)(area.Height * 0.7f)));
            Vector2 start = Client(new Vector2(drawn.X, drawn.Y));
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
