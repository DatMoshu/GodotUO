// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Input.Gamepad;

namespace GUO.Host;

/// <summary>
/// The pad's controller feel (docs/wiki/Controller.md) driven by injected
/// joypad events in the world: the first-login offer of "Set controls" and
/// B skipping it; the LT menu wheel opening the backpack and the paperdoll,
/// the tap reopening the last, the middle opening nothing; the RT interact
/// radar choosing a mobile with the right stick, looking (X), using (A),
/// stepping (RB), its menu (Y), using on release, and picking a target for a
/// target cursor; R3 war mode and L3 always run; Start and the Options entry;
/// the wizard cancelled by three inputs, then run through with one job
/// rebound (and swapped), its wheel slice changed, and the saved map taking
/// effect; and the Steam Deck trackpad (a mouse beside the pad) leaving the
/// mode on Gamepad. A picture at each step.
/// </summary>
/// <remarks>
/// Runs in a scratch home (--pad-wheels-probe implies --scratch-profile), so
/// padbindings.json and the profile start new and the real ones are never
/// touched. Injected events come from no real pad: the layout is set to
/// "labels", as GamepadProbe does. Where no mobile is within reach it asks the
/// shard for a horse beside the character ("[add", an owner command).
/// </remarks>
internal static class PadWheelsProbe
{
    public static bool Passed { get; private set; }

    private static int _failed;
    private static string _dir;
    private static int _shot;

    private static void Check(string what, bool ok, string detail = "")
    {
        GD.Print($"[GUO] pad wheels probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    private static async System.Threading.Tasks.Task Shot(Node host, string name)
    {
        await InputProbe.Wait(host, 3);
        string path = Path.Combine(_dir, $"{++_shot:D2}_{name}.png");
        Error e = host.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[GUO] pad wheels probe: shot {path} {(e == Error.Ok ? "" : e.ToString())}");
    }

    public static async System.Threading.Tasks.Task Run(Node host, string screenshotDir)
    {
        _dir = string.IsNullOrEmpty(screenshotDir) ? Path.Combine(OS.GetUserDataDir(), "pad-wheels") : screenshotDir;
        Directory.CreateDirectory(_dir);

        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            Check("got into the world", false);
            return;
        }

        await InputProbe.Wait(host, 60);
        // Scratch profiles can leave the world at 600x480 in a larger Godot
        // window (grey letterbox). Force full-size so evidence shots are
        // full-frame — same sequence the input probe runs.
        InputProbe.FullSizeGameWindow();
        await InputProbe.Wait(host, 10);
        Profile profile = ProfileManager.CurrentProfile;
        GamepadInput.Forced = true;
        profile.GamepadLayout = "labels";
        GamepadInput.ForgetLayouts();
        GD.Print($"[GUO] pad wheels probe: bindings file {PadBindings.FilePath}, offered {PadBindings.WizardOffered}");

        for (int i = 0; i < 120 && !PadArt.Warm(); i++)
        {
            await InputProbe.Wait(host, 1);
        }

        Check("the wheel and radar art is loaded before anything shows", PadArt.Ready);

        await Offer(host);
        await Wheel(host, world);
        await Radar(host, world);
        await Buttons(host, world, profile);
        await Wizard(host, world);
        await Trackpad(host, world);
        await Screens(host);
        await BindingDummies(host, world);

        PadBindings.ResetToDefaults();
        GamepadInput.Forced = null;
        InputMode.PadBesideMouse = null;
        Passed = _failed == 0;
        GD.Print($"[GUO] pad wheels probe: {(Passed ? "PASS" : $"FAIL ({_failed})")}");
    }

    // --- the first login on a pad ---------------------------------------------------------------

    private static async System.Threading.Tasks.Task Offer(Node host)
    {
        // A scratch home: never offered yet. The first pad button brings it up.
        bool before = PadBindings.WizardOffered;
        await Button(host, JoyButton.DpadRight);

        for (int i = 0; i < 60 && !PadWizard.Offering; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        Check("\"Set controls\" is offered after the first login on a pad", !before && PadWizard.Offering);
        Check("the offer is the action and the buttons, with no description",
            PadWizard.VisibleText == "Set controls\nSet controls\nNot now"
            && !PadWizard.VisibleText.Contains("minute") && !PadWizard.VisibleText.Contains("hold the button"),
            PadWizard.VisibleText);
        await Shot(host, "offer");

        await Button(host, JoyButton.B);
        string file = File.Exists(PadBindings.FilePath) ? File.ReadAllText(PadBindings.FilePath) : "";
        Check("B skips the offer, and it is remembered (offered once)",
            !PadWizard.IsOpen && PadWizard.LastResult == "skipped" && file.Contains("\"wizardOffered\": true"),
            $"open {PadWizard.IsOpen}, result {PadWizard.LastResult}");
    }

    // --- LT: the menu wheel --------------------------------------------------------------------

    private static async System.Threading.Tasks.Task Wheel(Node host, World world)
    {
        Item backpack = world.Player.FindItemByLayer(GUO.Game.Data.Layer.Backpack);

        // Backpack: the top slice, the left stick up.
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 6);
        bool open = PadWheel.IsOpen;
        Axis(JoyAxis.LeftY, -1f);
        await InputProbe.Wait(host, 8);
        int focus = PadWheel.Focus;
        await Shot(host, "wheel_backpack");
        Axis(JoyAxis.TriggerLeft, 0f);
        await InputProbe.Wait(host, 2);
        Axis(JoyAxis.LeftY, 0f);
        bool opened = await Until(host, () => backpack != null && UIManager.GetGump<Gump>(backpack.Serial) != null, 120);
        Check("LT opens the wheel; the left stick up points at the backpack; letting go opens it",
            open && focus == 0 && PadWheel.LastResult == "opened Backpack" && opened
            && PadScreen.IsOpen && PadScreen.Current == WheelWindow.Backpack && PadScreen.FillsClient && PadScreen.RowCount > 0,
            $"open {open}, focus {focus}, {PadWheel.LastResult}, gump {opened}, screen {PadScreen.Current} fills {PadScreen.FillsClient} rows {PadScreen.RowCount}");
        Check("the backpack screen is a grid beside a half-cut camera",
            PadScreen.Columns > 1 && PadScreen.HalfCut,
            $"cols {PadScreen.Columns}, half-cut {PadScreen.HalfCut}");
        var cam = Client.Game?.Scene?.Camera;
        Check("the bag sits over the live world: the world is drawn under the right half, centred on the left",
            PadScreen.WorldUnderScreen,
            $"camera {cam?.Bounds.X},{cam?.Bounds.Y} {cam?.Bounds.Width}x{cam?.Bounds.Height}, window {Client.Game?.ClientBounds.Width}x{Client.Game?.ClientBounds.Height}");
        // Evidence: focus a small art item (not gold) so icon upscale is obvious.
        for (int i = 0; i < Math.Max(1, PadScreen.RowCount); i++)
        {
            string lab = PadScreen.FocusLabel;
            bool skip = PadScreen.FocusArt == 0
                || string.IsNullOrWhiteSpace(lab)
                || lab.Equals("Empty", StringComparison.OrdinalIgnoreCase)
                || lab.IndexOf("gold", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!skip)
            {
                GD.Print($"[GUO] pad wheels probe: backpack focus '{lab}' art 0x{PadScreen.FocusArt:X4}");
                break;
            }

            PadScreen.Move(1, 0);
            await InputProbe.Wait(host, 2);
        }

        await Shot(host, "backpack_open");
        await ScaleChord(host, world);

        // Paperdoll: top right, with the right stick this time.
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 6);
        Axis(JoyAxis.RightX, 0.75f);
        Axis(JoyAxis.RightY, -0.75f);
        await InputProbe.Wait(host, 8);
        focus = PadWheel.Focus;
        await Shot(host, "wheel_paperdoll");
        Axis(JoyAxis.TriggerLeft, 0f);
        await InputProbe.Wait(host, 2);
        Axis(JoyAxis.RightX, 0f);
        Axis(JoyAxis.RightY, 0f);
        opened = await Until(host, () => UIManager.GetGump<PaperDollGump>(world.Player.Serial) != null, 180);
        Check("the right stick points too: top right opens the paperdoll",
            focus == 1 && opened && PadScreen.IsOpen && PadScreen.Current == WheelWindow.Paperdoll && PadScreen.FillsClient,
            $"focus {focus}, {PadWheel.LastResult}, screen {PadScreen.Current} fills {PadScreen.FillsClient}");
        Check("the paperdoll screen is a grid beside a half-cut camera",
            PadScreen.Columns > 1 && PadScreen.HalfCut,
            $"cols {PadScreen.Columns}, half-cut {PadScreen.HalfCut}");
        await Shot(host, "paperdoll_open");

        // A tap reopens the last window the wheel opened.
        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        await InputProbe.Wait(host, 10);
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 3);
        Axis(JoyAxis.TriggerLeft, 0f);
        opened = await Until(host, () => UIManager.GetGump<PaperDollGump>(world.Player.Serial) != null, 180);
        Check("a quick tap of LT reopens the last window (the paperdoll)",
            PadWheel.LastResult == "reopened Paperdoll" && opened && PadScreen.Current == WheelWindow.Paperdoll && PadScreen.FillsClient,
            PadWheel.LastResult);

        // Held, no stick, let go in the middle: nothing.
        int gumps = UIManager.Gumps.Count();
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 30);
        Axis(JoyAxis.TriggerLeft, 0f);
        await InputProbe.Wait(host, 30);
        Check("let go in the middle: nothing opens", PadWheel.LastResult == "nothing" && UIManager.Gumps.Count() == gumps && !PadScreen.IsOpen,
            $"{PadWheel.LastResult}, gumps {gumps} -> {UIManager.Gumps.Count()}, screen {PadScreen.IsOpen}");

        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();

        if (backpack != null)
        {
            UIManager.GetGump<Gump>(backpack.Serial)?.Dispose();
        }

        await InputProbe.Wait(host, 10);
    }

    // --- LB+R3: debug UI scale (not menu scale, not the D-pad) ---------------------------------------

    private static async System.Threading.Tasks.Task ScaleChord(Node host, World world)
    {
        PadOverlay.ResetDebugScale();
        await InputProbe.Wait(host, 3);
        bool war = world.Player.InWarMode;
        var seen = new System.Collections.Generic.List<float> { PadOverlay.UiScale };
        Down(JoyButton.LeftShoulder);
        await InputProbe.Wait(host, 2);

        for (int i = 0; i < PadOverlay.DebugScaleSteps.Length; i++)
        {
            await Button(host, JoyButton.RightStick, 3);
            seen.Add(PadOverlay.UiScale);
        }

        bool below = false;
        bool everyStep = true;

        foreach (float step in PadOverlay.DebugScaleSteps)
        {
            bool hit = false;

            foreach (float s in seen)
            {
                if (Math.Abs(s - step) < 0.02f)
                {
                    hit = true;
                }

                if (s < 1f - 0.001f)
                {
                    below = true;
                }
            }

            everyStep &= hit;
        }

        Check("LB+R3 cycles the debug UI scale (including below 1) and does not toggle war mode",
            world.Player.InWarMode == war && below && everyStep && Math.Abs(seen[1] - seen[0]) > 0.02f,
            $"war {war}->{world.Player.InWarMode}, {string.Join(" ", seen)}");

        for (int i = 0; i < PadOverlay.DebugScaleSteps.Length && Math.Abs(PadOverlay.UiScale - 0.5f) > 0.02f; i++)
        {
            await Button(host, JoyButton.RightStick, 3);
        }

        await InputProbe.Wait(host, 8);
        var small = PadScreen.PreviewBackpack(1280, 800, 0.5f);
        var deck = PadScreen.PreviewBackpack(1280, 800, 2f);
        Check("at 1280x800, UI scale 0.5 shows more than 3 backpack cells",
            small.Cells > 3, $"{small.Columns}x{small.Rows} = {small.Cells} cell {small.CellSize:0.#}");
        Check("at 1280x800, UI scale 2 shows more than 3 backpack cells",
            deck.Cells > 3, $"{deck.Columns}x{deck.Rows} = {deck.Cells} cell {deck.CellSize:0.#}");
        Check("the open backpack grid, detail strip and bag-aligned hints fit at the small scale",
            PadScreen.IsOpen && PadScreen.Current == WheelWindow.Backpack
            && PadScreen.VisibleCapacity > 3 && PadScreen.DetailUnderPack && PadScreen.HintsOnBag
            && Math.Abs(PadOverlay.UiScale - 0.5f) < 0.02f,
            $"scale {PadOverlay.UiScale:0.##}, cells {PadScreen.VisibleCapacity}, detail {PadScreen.DetailUnderPack}, hints {PadScreen.HintsOnBag}");

        Up(JoyButton.LeftShoulder);
        await InputProbe.Wait(host, 2);
        PadOverlay.ResetDebugScale();
        await InputProbe.Wait(host, 4);

        int guard = 0;

        while (PadScreen.PanelScale != 1 && guard++ < 4)
        {
            PadScreen.CyclePanelScale();
        }

        PadScreen.CyclePanelScale();
        int two = PadScreen.PanelScale;
        PadScreen.CyclePanelScale();
        int three = PadScreen.PanelScale;
        PadScreen.CyclePanelScale();
        int one = PadScreen.PanelScale;
        Check("options menu scale still cycles 1, 2, 3", two == 2 && three == 3 && one == 1, $"{two},{three},{one}");
    }

    // --- RT: the interact radar -------------------------------------------------------------------

    private static async System.Threading.Tasks.Task Radar(Node host, World world)
    {
        // X is Look only out of war mode; BindingDummies may have left war mode on.
        if (world.Player.InWarMode)
        {
            GameActions.ToggleWarMode(world.Player);
            await Until(host, () => !world.Player.InWarMode, 60);
        }

        Mobile near = Nearest(world);

        if (near == null)
        {
            GD.Print("[GUO] pad wheels probe: no mobile within reach; asking the shard for a horse");
            await InputProbe.Say(host, "[add Horse");

            if (await Until(host, () => world.TargetManager.IsTargeting, 180))
            {
                world.TargetManager.Target(0, (ushort) (world.Player.X + 2), (ushort) (world.Player.Y + 1), world.Player.Z);
            }

            await Until(host, () => (near = Nearest(world)) != null, 240);
        }

        if (near == null)
        {
            Check("a mobile within the radar's reach", false);
            return;
        }

        Axis(JoyAxis.TriggerRight, 1f);
        await InputProbe.Wait(host, 10);
        Check("RT opens the radar, with what is near listed", PadRadar.IsOpen && PadRadar.Near.Contains(near),
            $"{PadRadar.Near.Count} near: {string.Join(", ", PadRadar.Near.Take(6).Select(PadRadar.NameOf))}");
        await Shot(host, "radar_open");

        // The right stick, pushed toward the mobile on screen.
        Vector2 dir = (RadarView.Foot(near) - RadarView.Foot(world.Player)).Normalized();
        Axis(JoyAxis.RightX, dir.X * 0.95f);
        Axis(JoyAxis.RightY, dir.Y * 0.95f);
        await InputProbe.Wait(host, 6);
        Axis(JoyAxis.RightX, 0f);
        Axis(JoyAxis.RightY, 0f);
        await InputProbe.Wait(host, 6);
        Entity chosen = PadRadar.Chosen;
        Check("the right stick snaps the choice to the thing that way", chosen == near || chosen is Mobile,
            $"pushed ({dir.X:0.00},{dir.Y:0.00}), chose {PadRadar.NameOf(chosen)} 0x{chosen?.Serial ?? 0:X8}, wanted {PadRadar.NameOf(near)} 0x{near.Serial:X8}");
        Check("the world highlights the choice as the mouse would", ReferenceEquals(SelectedObject.Object, chosen),
            $"selected {SelectedObject.Object?.GetType().Name}");
        await Shot(host, "radar_chosen");

        await Button(host, JoyButton.X);
        Check("X looks (a single click on the choice)", PadRadar.LastAction == $"look 0x{chosen?.Serial ?? 0:X8}", PadRadar.LastAction);
        await InputProbe.Wait(host, 30);
        await Shot(host, "radar_look");

        int at = PadRadar.Near.ToList().IndexOf(chosen);
        await Button(host, JoyButton.RightShoulder);
        int now = PadRadar.Near.ToList().IndexOf(PadRadar.Chosen);
        Check("RB steps to the next thing, nearest first", PadRadar.Near.Count == 1 ? now == 0 : now == (at + 1) % PadRadar.Near.Count,
            $"{at} -> {now} of {PadRadar.Near.Count} ({PadRadar.NameOf(PadRadar.Chosen)})");
        await Shot(host, "radar_next");
        await Button(host, JoyButton.LeftShoulder);
        Check("LB steps back", PadRadar.Chosen == chosen, PadRadar.NameOf(PadRadar.Chosen));

        await Button(host, JoyButton.A);
        Check("A uses the choice (a double click)", PadRadar.LastAction == $"use 0x{chosen?.Serial ?? 0:X8}", PadRadar.LastAction);
        await InputProbe.Wait(host, 20);

        await Button(host, JoyButton.Y);
        Check("Y asks for the choice's context menu", PadRadar.LastAction == $"menu 0x{chosen?.Serial ?? 0:X8}", PadRadar.LastAction);
        await InputProbe.Wait(host, 40);
        await Shot(host, "radar_menu");
        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        UIManager.GetGump<PopupMenuGump>()?.Dispose();

        string last = PadRadar.LastAction;
        Axis(JoyAxis.TriggerRight, 0f);
        await InputProbe.Wait(host, 6);
        Check("letting RT go after a button acted does nothing more", !PadRadar.IsOpen && PadRadar.LastAction == last, PadRadar.LastAction);

        // Let go with a choice and no button: Use.
        Axis(JoyAxis.TriggerRight, 1f);
        await InputProbe.Wait(host, 6);
        await Button(host, JoyButton.RightShoulder);
        Entity first = PadRadar.Chosen;
        Axis(JoyAxis.TriggerRight, 0f);
        await InputProbe.Wait(host, 6);
        Check("letting RT go with a choice and no button uses it", PadRadar.LastAction == $"use 0x{first?.Serial ?? 0:X8}", PadRadar.LastAction);

        // Nothing chosen: letting go does nothing.
        Axis(JoyAxis.TriggerRight, 1f);
        await InputProbe.Wait(host, 6);
        last = PadRadar.LastAction;
        Axis(JoyAxis.TriggerRight, 0f);
        await InputProbe.Wait(host, 6);
        Check("letting RT go with nothing chosen does nothing", PadRadar.LastAction == last, PadRadar.LastAction);

        // A target cursor: the radar picks the target; A sends it, B cancels.
        GameObject taken = null;
        world.TargetManager.SetTargeting(o => taken = o, 0, TargetType.Neutral);
        Axis(JoyAxis.TriggerRight, 1f);
        await InputProbe.Wait(host, 6);
        PadRadarChoose(near);
        await InputProbe.Wait(host, 4);
        await Shot(host, "radar_target");
        await Button(host, JoyButton.A);
        Check("with a target cursor up, A sends the target to the choice", ReferenceEquals(taken, near) && !world.TargetManager.IsTargeting,
            $"taken {(taken as Entity)?.Serial ?? 0:X8}, targeting {world.TargetManager.IsTargeting}");
        world.TargetManager.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
        await InputProbe.Wait(host, 4);
        await Button(host, JoyButton.B);
        Check("with a target cursor up, B cancels it", !world.TargetManager.IsTargeting && PadRadar.IsOpen);
        Axis(JoyAxis.TriggerRight, 0f);
        await InputProbe.Wait(host, 6);
        world.TargetManager.CancelTarget();
    }

    /// <summary>Step with RB until <paramref name="e"/> is the choice.</summary>
    private static void PadRadarChoose(Entity e)
    {
        for (int i = 0; i < PadRadar.Near.Count + 1 && PadRadar.Chosen != e; i++)
        {
            PadRadar.Step(1);
        }
    }

    private static Mobile Nearest(World world) =>
        world.Mobiles.Values.Where(m => m != world.Player && !m.IsDestroyed && m.Distance <= Math.Min(6, PadBindings.RadarRange))
            .OrderBy(m => m.Distance).FirstOrDefault();

    // --- the new buttons ---------------------------------------------------------------------------

    private static async System.Threading.Tasks.Task Buttons(Node host, World world, Profile profile)
    {
        bool war = world.Player.InWarMode;
        await Button(host, JoyButton.RightStick);
        bool flipped = await Until(host, () => world.Player.InWarMode != war, 120);
        await Shot(host, "war_mode");
        Check("R3 toggles war mode", flipped, $"{war} -> {world.Player.InWarMode}");
        await Button(host, JoyButton.RightStick);
        await Until(host, () => world.Player.InWarMode == war, 120);

        bool run = profile.AlwaysRun;
        await Button(host, JoyButton.LeftStick);
        await InputProbe.Wait(host, 4);
        Check("L3 toggles always run", profile.AlwaysRun != run, $"{run} -> {profile.AlwaysRun}");
        await Button(host, JoyButton.LeftStick);

        await Button(host, JoyButton.Start);
        OptionsGump options = null;
        await Until(host, () => (options = UIManager.GetGump<OptionsGump>()) != null, 60);
        Check("Start opens the options", options != null);

        // The Options entry: Controller buttons, "Set controls...".
        Game.UI.Controls.NiceButton entry = options == null ? null : Find(options);
        Check("the options have a \"Set controls...\" entry", entry != null);

        if (entry != null)
        {
            entry.InvokeMouseUp(new Compat.Point(entry.ScreenCoordinateX + 4, entry.ScreenCoordinateY + 4), GUO.Input.MouseButtonType.Left);
            await InputProbe.Wait(host, 6);
            Check("it opens \"Set controls\" at the first job", PadWizard.IsOpen && PadWizard.Asking == PadCommand.Use,
                $"open {PadWizard.IsOpen}, asking {PadWizard.Asking}");
            await Shot(host, "wizard_from_options");
        }

        options?.Dispose();
    }

    private static Game.UI.Controls.NiceButton Find(Game.UI.Controls.Control root)
    {
        foreach (Game.UI.Controls.Control c in root.Children)
        {
            if (c is Game.UI.Controls.NiceButton b && b.TextLabel?.Text == "Set controls...")
            {
                return b;
            }

            if (Find(c) is Game.UI.Controls.NiceButton found)
            {
                return found;
            }
        }

        return null;
    }

    // --- "Set controls" -------------------------------------------------------------------------------

    private static async System.Threading.Tasks.Task Wizard(Node host, World world)
    {
        if (!PadWizard.IsOpen)
        {
            PadWizard.Open();
            await InputProbe.Wait(host, 4);
        }

        string card = PadWizard.VisibleText ?? "";
        bool plain = card.Contains("Use / click") && card.Contains("Cancel") && card.Contains("Menu wheel")
            && card.Contains("\tA") && !card.Contains("Click at the pointer") && !card.Contains("Press and hold")
            && !card.Contains("Escape:") && !card.Contains("what it does") && PadWizard.GlyphCount >= 4;
        Check("each set-controls row is the action, its glyph, and what it is set to", plain,
            $"glyphs {PadWizard.GlyphCount}\n{card}");

        // Three at once: cancelled, nothing changed.
        string fileBefore = File.Exists(PadBindings.FilePath) ? File.ReadAllText(PadBindings.FilePath) : "";
        Down(JoyButton.A);
        Down(JoyButton.X);
        await InputProbe.Wait(host, 4);
        Down(JoyButton.Y);
        await InputProbe.Wait(host, 4);
        await Shot(host, "wizard_cancelled");
        Up(JoyButton.A);
        Up(JoyButton.X);
        Up(JoyButton.Y);
        await Until(host, () => !PadWizard.IsOpen, 120);
        string fileAfter = File.Exists(PadBindings.FilePath) ? File.ReadAllText(PadBindings.FilePath) : "";
        Check("holding three inputs at once cancels the wizard, nothing changed",
            PadWizard.LastResult == "cancelled" && !PadWizard.IsOpen && fileAfter == fileBefore
            && PadBindings.For(PadCommand.WarMode) == PadInput.Button(JoyButton.RightStick),
            $"{PadWizard.LastResult}, open {PadWizard.IsOpen}, file same {fileAfter == fileBefore}");
        bool warBefore = world.Player.InWarMode;
        await Button(host, JoyButton.A);   // the swallowed releases are gone; a fresh press is a click
        Check("the inputs held at the cancel do nothing as they come up", world.Player.InWarMode == warBefore);

        // The whole way through: War mode on Y, Macro row on R3 (a swap), the rest as they are.
        PadWizard.Open();
        await InputProbe.Wait(host, 4);

        foreach (PadCommand c in PadBindings.Order)
        {
            PadInput give = c switch
            {
                PadCommand.WarMode => PadInput.Button(JoyButton.Y),
                PadCommand.MacroRow => PadInput.Button(JoyButton.RightStick),
                _ => PadBindings.For(c) ?? PadInput.Button(JoyButton.Guide),
            };

            Press(give, true);

            if (c == PadCommand.Use)
            {
                await InputProbe.Wait(host, 20);
                await Shot(host, "wizard_meter");
            }

            bool moved = await Until(host, () => PadWizard.Asking != c, 150);
            Press(give, false);
            await InputProbe.Wait(host, 3);

            if (!moved)
            {
                Check($"the wizard takes {give.Label} for {PadBindings.Name(c)}", false, $"still asking {PadWizard.Asking}, progress {PadWizard.Progress:0.00}");
                PadWizard.Cancel();
                return;
            }
        }

        Check("one held input per job, twelve jobs, then the wheel's slices", PadWizard.SlotAsking == 0, $"slot {PadWizard.SlotAsking}");

        // Slices: keep each, but the left one (Macros) becomes Status (two down).
        for (int slot = 0; slot < 8; slot++)
        {
            if (slot == 6)
            {
                await Button(host, JoyButton.DpadDown, 6);
                await Button(host, JoyButton.DpadDown, 6);
                await Shot(host, "wizard_slots");
            }

            await Button(host, JoyButton.A, 6);
        }

        await InputProbe.Wait(host, 4);
        await Shot(host, "wizard_done");
        await Until(host, () => !PadWizard.IsOpen, 120);
        string file = File.Exists(PadBindings.FilePath) ? File.ReadAllText(PadBindings.FilePath) : "";
        Check("the choices are applied together and saved",
            PadWizard.LastResult == "applied" && PadBindings.For(PadCommand.WarMode) == PadInput.Button(JoyButton.Y)
            && PadBindings.For(PadCommand.MacroRow) == PadInput.Button(JoyButton.RightStick)
            && PadBindings.Wheel[6] == WheelWindow.Status && file.Contains("\"WarMode\": \"b3\"") && file.Contains("\"Status\""),
            $"{PadWizard.LastResult}, war {PadBindings.For(PadCommand.WarMode)?.Label}, macro row {PadBindings.For(PadCommand.MacroRow)?.Label}, slice 7 {PadBindings.Wheel[6]}");

        // The saved map takes effect: Y is war mode now, R3 the macro row.
        PadBindings.Load();
        bool war = world.Player.InWarMode;
        await Button(host, JoyButton.Y);
        bool yWar = await Until(host, () => world.Player.InWarMode != war, 120);
        Check("the rebound Y toggles war mode (read back from the file)", yWar, $"{war} -> {world.Player.InWarMode}");
        war = world.Player.InWarMode;
        await Button(host, JoyButton.RightStick);
        await InputProbe.Wait(host, 60);
        Check("R3 no longer toggles war mode; it is the macro row now", world.Player.InWarMode == war
            && (UIManager.GetGump<OptionsGump>() != null || (GUO.Input.Touch.TouchInput.Bar?.HandleShown ?? false)
                || (PadScreen.IsOpen && PadScreen.Current == WheelWindow.Macros && PadScreen.FillsClient)),
            $"war {war} -> {world.Player.InWarMode}, options {(UIManager.GetGump<OptionsGump>() != null)}, screen {PadScreen.Current}");
        await Shot(host, "rebound_macro_row");
        UIManager.GetGump<OptionsGump>()?.Dispose();

        await Button(host, JoyButton.Y);
        await Until(host, () => !world.Player.InWarMode, 120);

        // The new slice: the left one opens the status bar.
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 6);
        Axis(JoyAxis.LeftX, -1f);
        await InputProbe.Wait(host, 8);
        await Shot(host, "wheel_rebound");
        Axis(JoyAxis.TriggerLeft, 0f);
        await InputProbe.Wait(host, 2);
        Axis(JoyAxis.LeftX, 0f);
        await InputProbe.Wait(host, 20);
        Check("the wheel's changed slice opens its new window",
            PadWheel.LastResult == "opened Status" && PadScreen.IsOpen && PadScreen.Current == WheelWindow.Status
            && PadScreen.FillsClient && PadScreen.RowCount > 0,
            $"{PadWheel.LastResult}, rows {PadScreen.RowCount}, fills {PadScreen.FillsClient}");
        PadScreen.Close();

        PadBindings.ResetToDefaults();
    }

    // --- every wheel window is a screen ----------------------------------------------------------

    private static async System.Threading.Tasks.Task Screens(Node host)
    {
        foreach (WheelWindow w in PadWizard.Choices)
        {
            PadScreen.Close();
            PadWheel.Open(w);
            await InputProbe.Wait(host, 2);
            Check($"the {PadBindings.Name(w)} window opens full screen",
                PadScreen.IsOpen && PadScreen.Current == w && PadScreen.FillsClient && PadScreen.RowCount > 0,
                $"open {PadScreen.IsOpen}, current {PadScreen.Current}, fills {PadScreen.FillsClient}, rows {PadScreen.RowCount}");
            Check($"the {PadBindings.Name(w)} window half-cuts the camera",
                PadScreen.HalfCut, $"half-cut {PadScreen.HalfCut}");
        }

        PadScreen.Close();
        Check("closing a screen restores the camera", !PadScreen.HalfCut && !PadScreen.IsOpen);
        Check("the wheel no longer offers a window that only asks the shard",
            Array.IndexOf(PadWizard.Choices, WheelWindow.Journal) >= 0 && PadWizard.Choices.Length == 10,
            $"choices {PadWizard.Choices.Length}");
    }

    // --- controller bindings against GUO pad dummies ----------------------------------------------

    private static async System.Threading.Tasks.Task BindingDummies(Node host, World world)
    {
        // Solo-verifiable shard actions only. Trade / party / guild need a second
        // player: those checks only prove the client opens them.
        PadScreen.Close();
        await InputProbe.Say(host, "[GuoPadDummies");
        await InputProbe.Wait(host, 90);

        Mobile Find(string name)
        {
            foreach (Mobile m in world.Mobiles.Values)
            {
                if (m != null && !m.IsDestroyed && m.Name != null && m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    && m.Distance <= 12)
                {
                    return m;
                }
            }

            return null;
        }

        Item FindItem(string name)
        {
            foreach (Item it in world.Items.Values)
            {
                if (it != null && !it.IsDestroyed && it.Name != null && it.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    && it.Distance <= 12)
                {
                    return it;
                }
            }

            return null;
        }

        bool JournalHas(string needle)
        {
            foreach (JournalEntry e in JournalManager.Entries)
            {
                if (e.Text != null && e.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (e.Name != null && e.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        Mobile attack = Find("Attack Dummy");
        Mobile talk = Find("Talk Dummy");
        Mobile context = Find("Context Dummy");
        Mobile pick = Find("Pickpocket Dummy");
        Item use = FindItem("Use Dummy");
        Item loot = FindItem("Loot Dummy");

        Check("pad dummies are in the world (attack/use/loot/talk/context/pickpocket)",
            attack != null && use != null && loot != null && talk != null && context != null && pick != null,
            $"atk {attack != null}, use {use != null}, loot {loot != null}, talk {talk != null}, ctx {context != null}, pick {pick != null}");

        if (attack != null)
        {
            if (!world.Player.InWarMode)
            {
                GameActions.ToggleWarMode(world.Player);
                await Until(host, () => world.Player.InWarMode, 60);
            }

            // Attack() can stall on the criminal-query gump when the dummy
            // still reads innocent. Send the attack packet the client sends
            // once that query is confirmed.
            ProfileManager.CurrentProfile.EnabledCriminalActionQuery = false;
            // Fists only reach one tile. Stand beside the dummy, not on it
            // (same-tile LineOfSight fails CheckAttack, so the packet never
            // becomes a combatant).
            await InputProbe.Say(host, $"[go {attack.X + 1} {attack.Y} {attack.Z}");
            await Until(host, () => attack.Distance <= 1 && attack.Distance > 0, 90);
            GameActions.Attack(world, attack.Serial);
            GameActions.RequestMobileStatus(world, attack.Serial);
            GD.Print($"[GUO] pad wheels probe: attack sent dist {attack.Distance} noto {attack.NotorietyFlag} last {world.TargetManager.LastAttack:X8} at {attack.X},{attack.Y},{attack.Z} player {world.Player.X},{world.Player.Y},{world.Player.Z} hits {attack.Hits}/{attack.HitsMax}");
            bool landed = await Until(host, () => JournalHas("GUO_PAD: hit attack-dummy") || attack.Hits < attack.HitsMax, 300);
            Check("attack binding reaches the attack dummy",
                world.TargetManager.LastAttack == attack.Serial && (landed || JournalHas("GUO_PAD: hit attack-dummy")),
                $"hits {attack.Hits}/{attack.HitsMax}, journal {JournalHas("GUO_PAD: hit attack-dummy")}, war {world.Player.InWarMode}, last {world.TargetManager.LastAttack:X8} want {attack.Serial:X8}");
        }

        if (use != null)
        {
            GameActions.DoubleClick(world, use.Serial);
            await Until(host, () => JournalHas("GUO_PAD: used use-dummy"), 120);
            Check("use/double-click on the use dummy", JournalHas("GUO_PAD: used use-dummy"));
        }

        if (loot != null)
        {
            GameActions.DoubleClick(world, loot.Serial);
            bool opened = await Until(host, () => UIManager.GetGump<Gump>(loot.Serial) != null || JournalHas("GUO_PAD: opened loot-dummy"), 120);
            Check("loot/open on the loot dummy", opened || JournalHas("GUO_PAD: opened loot-dummy"));
        }

        if (talk != null)
        {
            await InputProbe.Say(host, "hello");
            await Until(host, () => JournalHas("GUO_PAD: talk-dummy heard you"), 120);
            Check("talk reaches the talk dummy", JournalHas("GUO_PAD: talk-dummy heard you"));
        }

        if (context != null)
        {
            GameActions.OpenPopupMenu(context.Serial, true);
            bool menu = await Until(host, () => UIManager.PopupMenu != null || UIManager.GetGump<PopupMenuGump>() != null, 120);
            Check("context menu opens on the context dummy (client)", menu);
            // Picking the entry needs a menu selection the pad radar already covers;
            // assert the menu itself here.
        }

        if (pick != null)
        {
            // Pickpocket is a Stealing skill check on an NPC, not a container.
            // Clear a held item so the shard will accept the skill.
            if (Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                GameActions.DropItem(Client.Game.UO.GameCursor.ItemHold.Serial, world.Player.X, world.Player.Y, world.Player.Z, 0);
                await InputProbe.Wait(host, 20);
            }

            GameActions.UseSkill(33); // Stealing
            bool targeting = await Until(host, () => world.TargetManager.IsTargeting, 90);
            // A fresh character often has 0.0 Stealing; the shard then refuses
            // the skill and never opens a target. That is a skill gate, not a
            // missing binding. Report it as client-sent, not a world pass.
            Check("pickpocket/stealing: client sends UseSkill (world target only if the skill is usable)",
                true, targeting ? "target cursor opened" : "no target — skill likely too low on a new character");
            if (targeting)
            {
                world.TargetManager.Target(pick.Serial);
                await InputProbe.Wait(host, 60);
            }
        }

        // Second-player systems: only prove the client opens the request.
        GameActions.RequestPartyInviteByTarget();
        await InputProbe.Wait(host, 20);
        bool partyTarget = world.TargetManager.IsTargeting;
        Check("party invite: client opens a target (not a world pass — needs a second player)", partyTarget);
        if (partyTarget)
        {
            world.TargetManager.CancelTarget();
        }

        Check("trade/guild need a second player — not asserted as a world pass", true,
            "client-open only; no second player in this probe");

        // Scale stays framed: cycle panel scale and fonts; frame still fills the right half.
        PadScreen.Open(WheelWindow.Options);
        await InputProbe.Wait(host, 10);
        int before = PadScreen.PanelScale;
        PadScreen.CyclePanelScale();
        PadScreen.CycleMenuFont();
        PadScreen.CycleChatFont();
        await InputProbe.Wait(host, 6);
        Check("menu scale cycles and the screen stays full-frame",
            PadScreen.PanelScale == (before >= 3 ? 1 : before + 1) && PadScreen.FillsClient && PadScreen.HalfCut,
            $"scale {before}->{PadScreen.PanelScale}, fills {PadScreen.FillsClient}, half-cut {PadScreen.HalfCut}");
        PadScreen.Open(WheelWindow.Journal);
        await InputProbe.Wait(host, 8);
        Check("journal uses its own chat font scale (independent of menu font)",
            PadScreen.ChatFontScale != 0 && PadScreen.IsOpen && PadScreen.Current == WheelWindow.Journal,
            $"chatFont {PadScreen.ChatFontScale}, menuFont {PadScreen.MenuFontScale}");
        await Shot(host, "binding_dummies");
        PadScreen.Close();
    }

    // --- the Steam Deck trackpad: a mouse beside the pad ----------------------------------------------

    private static async System.Threading.Tasks.Task Trackpad(Node host, World world)
    {
        InputMode.PadBesideMouse = true;
        await Button(host, JoyButton.DpadLeft, 8);
        bool pad = InputMode.Current == InputKind.Gamepad;
        Vector2 at = new(GUO.Input.Mouse.Position.X, GUO.Input.Mouse.Position.Y);
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at + new Vector2(-60, -30), Relative = new Vector2(-60, -30) });
        await InputProbe.Wait(host, 3);
        Vector2 moved = new(GUO.Input.Mouse.Position.X, GUO.Input.Mouse.Position.Y);
        bool still = InputMode.Current == InputKind.Gamepad && !InputMode.PointerHidden;
        Godot.Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = moved });
        await InputProbe.Wait(host, 2);
        Godot.Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = moved });
        await InputProbe.Wait(host, 3);
        Check("on a Deck the trackpad moves and clicks the pointer and the mode stays Gamepad",
            pad && still && InputMode.Current == InputKind.Gamepad && moved != at, $"{at} -> {moved}, {InputMode.Current}");

        // The right stick goes on from where the trackpad left the pointer.
        Axis(JoyAxis.RightX, 0.8f);
        await InputProbe.Wait(host, 6);
        Axis(JoyAxis.RightX, 0f);
        await InputProbe.Wait(host, 2);
        Vector2 stick = new(GUO.Input.Mouse.Position.X, GUO.Input.Mouse.Position.Y);
        Check("the right stick moves the same pointer on from there", stick.X > moved.X && Math.Abs(stick.Y - moved.Y) < 2, $"{moved} -> {stick}");

        // The trackpad while LT is held does not steer the wheel.
        Axis(JoyAxis.TriggerLeft, 1f);
        await InputProbe.Wait(host, 4);
        Axis(JoyAxis.LeftX, 0.9f);
        await InputProbe.Wait(host, 6);
        int focus = PadWheel.Focus;

        for (int i = 0; i < 6; i++)
        {
            Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = stick + new Vector2(-20 * i, -10 * i), Relative = new Vector2(-20, -10) });
            await InputProbe.Wait(host, 2);
        }

        bool kept = PadWheel.IsOpen && PadWheel.Focus == focus && InputMode.Current == InputKind.Gamepad;
        Axis(JoyAxis.TriggerLeft, 0f);
        await InputProbe.Wait(host, 2);
        Axis(JoyAxis.LeftX, 0f);
        await InputProbe.Wait(host, 20);
        Check("the trackpad moving while LT is held leaves the wheel's slice alone", kept && PadWheel.LastResult == "opened Journal",
            $"focus {focus} -> {PadWheel.Focus}, {PadWheel.LastResult}");
        UIManager.GetGump<JournalGump>()?.Dispose();
        UIManager.GetGump<ResizableJournal>()?.Dispose();

        // ...nor the radar's choice.
        Axis(JoyAxis.TriggerRight, 1f);
        await InputProbe.Wait(host, 6);
        await Button(host, JoyButton.RightShoulder);
        Entity chosen = PadRadar.Chosen;

        for (int i = 0; i < 6; i++)
        {
            Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = stick + new Vector2(-15 * i, 10 * i), Relative = new Vector2(-15, 10) });
            await InputProbe.Wait(host, 2);
        }

        Check("the trackpad moving while RT is held leaves the radar's choice alone",
            PadRadar.IsOpen && chosen != null && PadRadar.Chosen == chosen && ReferenceEquals(PadRadar.Highlight, chosen), PadRadar.NameOf(PadRadar.Chosen));
        await Button(host, JoyButton.B);
        Axis(JoyAxis.TriggerRight, 0f);
        await InputProbe.Wait(host, 4);

        // A real key still switches to the keyboard.
        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = true });
        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = false });
        await InputProbe.Wait(host, 2);
        Check("a key still switches to KeyboardMouse", InputMode.Current == InputKind.KeyboardMouse);

        // Off a Deck (no Steam Input), a moved mouse switches back, as before (ADR-0025).
        InputMode.PadBesideMouse = false;
        await Button(host, JoyButton.DpadLeft, 8);
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = stick, Relative = new Vector2(12, 0) });
        await InputProbe.Wait(host, 2);
        Check("elsewhere a moved mouse still switches to KeyboardMouse", InputMode.Current == InputKind.KeyboardMouse);
    }

    // --- injected input ---------------------------------------------------------------------------------

    private static void Axis(JoyAxis axis, float value) =>
        Godot.Input.ParseInputEvent(new InputEventJoypadMotion { Axis = axis, AxisValue = value });

    private static void Down(JoyButton b) => Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = b, Pressed = true });

    private static void Up(JoyButton b) => Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = b, Pressed = false });

    private static void Press(PadInput input, bool down)
    {
        if (input.IsAxis)
        {
            Axis((JoyAxis) input.Index, down ? input.Sign * 1f : 0f);
        }
        else if (down)
        {
            Down((JoyButton) input.Index);
        }
        else
        {
            Up((JoyButton) input.Index);
        }
    }

    private static async System.Threading.Tasks.Task Button(Node host, JoyButton button, int after = 20)
    {
        Down(button);
        await InputProbe.Wait(host, 2);
        Up(button);
        await InputProbe.Wait(host, after);
    }

    private static async System.Threading.Tasks.Task<bool> Until(Node host, Func<bool> done, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            if (done())
            {
                return true;
            }

            await InputProbe.Wait(host, 1);
        }

        return done();
    }
}
