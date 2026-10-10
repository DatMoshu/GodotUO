// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Input.Gamepad;

namespace GUO.Host;

/// <summary>
/// The gamepad layer without a gamepad: the layout table, then joypad
/// events pushed through Input.ParseInputEvent in the world -- the D-pad
/// and the left stick walk, B cancels a target cursor, A takes one.
/// </summary>
/// <remarks>
/// Injected joypad events come from no real pad, so the automatic layout
/// resolves to Unknown; the probe checks that, then sets the manual
/// "labels" choice as a player would under Options, and puts it back.
/// </remarks>
internal static class GamepadProbe
{
    public static bool Passed { get; private set; }

    /// <summary>
    /// Paced for a screen recording (--gamepad-clip): the run starts at the
    /// Britain bank, where the lamps light the street at night, and Y holds
    /// the command bar's second row open for three seconds. About 15 s.
    /// </summary>
    public static bool Clip;

    private const string ClipSpot = "[go 1434 1699 0";

    private static int _failed;

    private static void Check(string what, bool ok, string detail = "")
    {
        GD.Print($"[GUO] gamepad probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        // The table measured on the Thor (docs/thor-controller-layout.md).
        Check("Thor, Standard mode: printed labels", GamepadInput.Detect("Odin Controller", "AYN Thor", false) == GamepadLayout.Labels);
        Check("Thor, XBox mode: A/B and X/Y swapped", GamepadInput.Detect("Xbox Series X Controller", "AYN Thor", true) == GamepadLayout.Swapped);
        Check("Odin 2 Mini (not measured): unknown", GamepadInput.Detect("Odin Controller", "Odin2 Mini", false) == GamepadLayout.Unknown);
        Check("an Odin pad on another device: unknown", GamepadInput.Detect("Odin Controller", "Pixel 8", false) == GamepadLayout.Unknown);
        Check("an SDL-mapped Xbox pad on a PC: labels", GamepadInput.Detect("Xbox Series X Controller", "Windows PC", true) == GamepadLayout.Labels);
        Check("an unmapped pad: unknown", GamepadInput.Detect("Some Pad", "Windows PC", false) == GamepadLayout.Unknown);

        // Started as soon as Main is ready, before the client has booted.
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        if (!Client.Game.UO.World.InGame)
        {
            // On a device, as DualProbe logs in: the touch layer would
            // swallow the probe's clicks, which aim in client pixels.
            bool touchOn = GUO.Input.Touch.TouchInput.Enabled;
            GUO.Input.Touch.TouchInput.Enabled = false;
            InputProbe.PointerScale = Client.Game.DpiScale;
            await InputProbe.EnterTheWorld(host, 200);
            InputProbe.PointerScale = 1f;
            GUO.Input.Touch.TouchInput.Enabled = touchOn;
        }

        if (!Client.Game.UO.World.InGame)
        {
            Check("got into the world", false);

            return;
        }

        await InputProbe.Wait(host, 60);

        // ADR-0025 (the owner, 2026-09-28): a pad works by default on every
        // platform, the input mode follows whatever was used last, the
        // pointer hides while the pad is idle, and "Use a controller" off
        // makes a pad do nothing. Then the gate is forced open for the rest.
        {
            var profile0 = ProfileManager.CurrentProfile;
            var cursor = Client.Game.UO.World.TargetManager;
            bool kept = profile0.Gamepad;
            int changes = 0;
            System.Action<GUO.Input.InputKind> count = _ => changes++;
            GUO.Input.InputMode.Changed += count;

            profile0.Gamepad = true;
            GUO.Input.InputMode.Switch(GUO.Input.InputKind.KeyboardMouse);
            changes = 0;
            (ushort, ushort) at = Where();
            await Hold(host, new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = true },
                new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = false });
            Check("a pad works by default: the D-pad walks", new Profile().Gamepad && GamepadInput.Enabled && Where() != at,
                $"default {new Profile().Gamepad}, at {at} -> {Where()}");
            Check("a pad button switches the input mode to Gamepad, once",
                GUO.Input.InputMode.Current == GUO.Input.InputKind.Gamepad && changes == 1,
                $"{GUO.Input.InputMode.Current}, {changes} change(s), pad \"{GUO.Input.InputMode.PadName}\" {GUO.Input.InputMode.PadFamily}");

            Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = true });
            Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = false });
            await InputProbe.Wait(host, 2);
            Check("a key switches it back to KeyboardMouse at once", GUO.Input.InputMode.Current == GUO.Input.InputKind.KeyboardMouse && changes == 2,
                $"{GUO.Input.InputMode.Current}, {changes} change(s)");

            // Guide: a button bound to no job (Start opens the options now), so it only switches the mode.
            await Button(host, JoyButton.Guide);
            Vector2 mouse = new Vector2(GUO.Input.Mouse.Position.X, GUO.Input.Mouse.Position.Y);
            Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = mouse, Relative = new Vector2(1, 0) });
            await InputProbe.Wait(host, 2);
            bool stillPad = GUO.Input.InputMode.Current == GUO.Input.InputKind.Gamepad;
            Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = mouse, Relative = new Vector2(12, 0) });
            await InputProbe.Wait(host, 2);
            Check("a mouse moved a few pixels switches it back; a 1 px jitter does not",
                stillPad && GUO.Input.InputMode.Current == GUO.Input.InputKind.KeyboardMouse, $"after 1 px: pad {stillPad}; after 12 px: {GUO.Input.InputMode.Current}");

            await Button(host, JoyButton.Guide);
            bool shownAtFirst = !GUO.Input.InputMode.PointerHidden;
            await InputProbe.Wait(host, 300);
            bool hiddenIdle = GUO.Input.InputMode.PointerHidden;
            await Hold(host, new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 0.8f },
                new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 0f });
            bool shownOnStick = !GUO.Input.InputMode.PointerHidden;
            await InputProbe.Wait(host, 300);
            cursor.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
            bool shownTargeting = !GUO.Input.InputMode.PointerHidden;
            cursor.CancelTarget();
            Check("the pointer: shown on switching to the pad, hidden when idle, back on the right stick, kept for a target cursor",
                shownAtFirst && hiddenIdle && shownOnStick && shownTargeting,
                $"first {shownAtFirst}, idle hidden {hiddenIdle}, stick {shownOnStick}, targeting {shownTargeting}");

            profile0.Gamepad = false;
            at = Where();
            await Hold(host, new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = true },
                new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = false });
            cursor.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
            await Button(host, JoyButton.B);
            Check("\"Use a controller\" off: a pad does nothing (no walk, no cancel)",
                Where() == at && cursor.IsTargeting, $"at {at} -> {Where()}, targeting {cursor.IsTargeting}");
            cursor.CancelTarget();

            profile0.Gamepad = kept;
            GUO.Input.InputMode.Changed -= count;
        }

        GamepadInput.Forced = true;

        if (Clip)
        {
            await InputProbe.Say(host, ClipSpot);
            await InputProbe.Wait(host, 120);
            GD.Print("[GUO] gamepad probe: clip, at the Britain bank");
        }

        Profile profile = ProfileManager.CurrentProfile;
        string manual = profile.GamepadLayout;
        var targets = Client.Game.UO.World.TargetManager;

        // Unknown: B does nothing, and says where to choose.
        profile.GamepadLayout = "auto";
        GamepadInput.ForgetLayouts();
        // Only where the pad is unknown: the desktop's injected one, the Odin's.
        // The Thor's own pad resolves, and there B cancels, as it should.
        GamepadLayout auto = GamepadInput.Resolve(0);

        if (auto == GamepadLayout.Unknown)
        {
            targets.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
            await Button(host, JoyButton.B);
            Check("an unknown pad's B does not cancel", targets.IsTargeting);
            targets.CancelTarget();
        }
        else
        {
            GD.Print($"[GUO] gamepad probe: skip  the unknown-pad check (this pad resolves to {auto})");
        }

        profile.GamepadLayout = "labels";
        GamepadInput.ForgetLayouts();

        // Walking.
        (ushort x, ushort y) start = Where();
        await Hold(host, new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = false });
        (ushort x, ushort y) afterDpad = Where();
        Check("the D-pad walks", afterDpad != start, $"{start} -> {afterDpad}");

        await Hold(host, new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = -1f },
            new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 0f });
        (ushort x, ushort y) afterStick = Where();
        Check("the left stick walks", afterStick != afterDpad, $"{afterDpad} -> {afterStick}");

        // B cancels a target cursor.
        targets.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
        await Button(host, JoyButton.B);
        Check("B cancels a target cursor", !targets.IsTargeting);

        // A takes one where the pointer is: on the character. On a device the
        // pointer is placed as the login is typed: touch aside, client pixels
        // scaled to the window.
        bool touchWas = GUO.Input.Touch.TouchInput.Enabled;
        GUO.Input.Touch.TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;
        Vector2? character = await InputProbe.FindCharacter(host);

        if (character == null)
        {
            Check("the character is on screen for A", false);
        }
        else
        {
            GUO.Input.GodotInput.Handle(new InputEventMouseMotion { Position = character.Value * InputProbe.PointerScale });
            bool taken = false;
            targets.SetTargeting(o => taken = o != null, 0, TargetType.Neutral);
            await Button(host, JoyButton.A);
            Check("A takes a target under the pointer", taken && !targets.IsTargeting);
        }

        InputProbe.PointerScale = 1f;
        GUO.Input.Touch.TouchInput.Enabled = touchWas;

        // Y taps the command bar's handle: one row opens to two, and back.
        // The bar is there only with the touch layer on (a device).
        var bar = GUO.Input.Touch.TouchInput.Bar;

        if (bar != null && bar.HandleShown)
        {
            int rows = bar.RowsOpen;
            await Button(host, JoyButton.Y);
            await InputProbe.Wait(host, Clip ? 180 : 30);
            int opened = bar.RowsOpen;
            await Button(host, JoyButton.Y);
            await InputProbe.Wait(host, Clip ? 90 : 30);
            Check("Y opens and closes the command bar's rows, as the handle does",
                opened != rows && bar.RowsOpen == rows, $"{rows} -> {opened} -> {bar.RowsOpen}");
        }
        else
        {
            GD.Print("[GUO] gamepad check: skip Y on the command bar (no touch bar, or its rows are off)");
        }

        await WindowMenu(host, profile);

        profile.GamepadLayout = manual;
        GamepadInput.ForgetLayouts();
        GamepadInput.Forced = null;
        Passed = _failed == 0;
        GD.Print($"[GUO] gamepad probe: {(Passed ? "PASS" : $"FAIL ({_failed})")}");
    }

    /// <summary>
    /// X opens the window menu for the topmost window and closes it again; A
    /// presses the card's control under the pointer; B closes it. The menu is
    /// mobile only, so on a desktop the check turns on the mobile window
    /// controls for its duration.
    /// </summary>
    private static async System.Threading.Tasks.Task WindowMenu(Node host, Profile profile)
    {
        var world = Client.Game.UO.World;
        bool mobile = profile.MobileWindowControls;
        profile.MobileWindowControls = true;
        Game.UI.Gumps.PaperDollGump doll = null;

        try
        {
            GUO.Input.Touch.WindowMenu.Close();
            Game.GameActions.OpenPaperdoll(world, world.Player.Serial);

            for (int i = 0; i < 180 && (doll = UIManager.GetGump<Game.UI.Gumps.PaperDollGump>(world.Player.Serial)) == null; i++)
            {
                await InputProbe.Wait(host, 1);
            }

            if (doll == null)
            {
                Check("a paperdoll to open the window menu on", false);

                return;
            }

            UIManager.MakeTopMostGump(doll);
            await InputProbe.Wait(host, 10);

            await Button(host, JoyButton.X);
            var opened = GUO.Input.Touch.WindowMenu.Target;
            Check("X opens the window menu for the topmost window",
                GUO.Input.Touch.WindowMenu.IsOpen && opened != null, $"target {opened?.GetType().Name ?? "none"}");

            if (!GUO.Input.Touch.WindowMenu.IsOpen)
            {
                return;
            }

            // A on the card's + steps the size, as a tap does. Only on the main
            // screen: the pointer never reaches the second (the D-pad does, below).
            await InputProbe.Wait(host, 12);
            float before = opened.PresentationScale;

            if (GUO.Input.Touch.WindowMenu.OnSecondScreen)
            {
                GD.Print("[GUO] gamepad probe: skip  A under the pointer (the card is on the second screen)");
            }
            else
            {
                Vector2? plus = GUO.Input.Touch.WindowMenu.ButtonCentre("+");

                if (plus != null)
                {
                    GUO.Input.GodotInput.Handle(new InputEventMouseMotion { Position = plus.Value * Client.Game.DpiScale });
                    await InputProbe.Wait(host, 2);
                    await Button(host, JoyButton.A);
                }

                Check("A presses the window menu's + under the pointer",
                    plus != null && opened.PresentationScale > before + 0.01f,
                    $"{before} -> {opened.PresentationScale}, + at {plus?.ToString() ?? "none"}, card {GUO.Input.Touch.WindowMenu.CardRect}");
                opened.PresentationScale = before;
            }

            // The D-pad selects the card's controls, and A presses the selected
            // one: what reaches a card on the Thor's second screen, which the
            // pointer cannot.
            (ushort, ushort) stood = Where();
            await Button(host, JoyButton.DpadDown);
            Control first = GUO.Input.Touch.WindowMenu.FocusOwner;
            await Button(host, JoyButton.DpadDown);
            Control second = GUO.Input.Touch.WindowMenu.FocusOwner;
            Check("the D-pad selects the window menu's controls, not walking",
                first != null && second != null && second != first && Where() == stood,
                $"{Name(first)} -> {Name(second)}, at {stood} -> {Where()}, card {GUO.Input.Touch.WindowMenu.CardRect}, open {GUO.Input.Touch.WindowMenu.IsOpen}");

            // + sits right of - on the stepper row, above the slider.
            for (int i = 0; i < 12 && Name(GUO.Input.Touch.WindowMenu.FocusOwner) != "+"; i++)
            {
                await Button(host, Name(GUO.Input.Touch.WindowMenu.FocusOwner) == "-" ? JoyButton.DpadRight : JoyButton.DpadUp);
            }

            before = opened.PresentationScale;
            await Button(host, JoyButton.A);
            Check("A presses the selected control (+)", opened.PresentationScale > before + 0.01f,
                $"selected {Name(GUO.Input.Touch.WindowMenu.FocusOwner)}, {before} -> {opened.PresentationScale}");
            opened.PresentationScale = before;

            await Button(host, JoyButton.B);
            Check("B closes the window menu", !GUO.Input.Touch.WindowMenu.IsOpen && !opened.IsDisposed);

            await Button(host, JoyButton.X);
            bool again = GUO.Input.Touch.WindowMenu.IsOpen;
            await Button(host, JoyButton.X);
            Check("X closes the window menu it opened", again && !GUO.Input.Touch.WindowMenu.IsOpen, $"opened {again}");
        }
        finally
        {
            GUO.Input.Touch.WindowMenu.Close();
            doll?.Dispose();
            profile.MobileWindowControls = mobile;
        }
    }

    private static string Name(Control c) => c == null ? "none" : c is Button b ? b.Text : c.GetType().Name;

    private static (ushort, ushort) Where() => (Client.Game.UO.World.Player.X, Client.Game.UO.World.Player.Y);

    private static async System.Threading.Tasks.Task Button(Node host, JoyButton button)
    {
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = true });
        await InputProbe.Wait(host, 2);
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = false });
        await InputProbe.Wait(host, 30);
    }

    private static async System.Threading.Tasks.Task Hold(Node host, InputEvent down, InputEvent up)
    {
        Godot.Input.ParseInputEvent(down);
        await InputProbe.Wait(host, 90);
        Godot.Input.ParseInputEvent(up);
        await InputProbe.Wait(host, 60);
    }
}
