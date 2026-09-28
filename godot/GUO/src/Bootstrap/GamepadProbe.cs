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
            await InputProbe.EnterTheWorld(host, 200);
        }

        if (!Client.Game.UO.World.InGame)
        {
            Check("got into the world", false);

            return;
        }

        await InputProbe.Wait(host, 60);

        Profile profile = ProfileManager.CurrentProfile;
        string manual = profile.GamepadLayout;
        var targets = Client.Game.UO.World.TargetManager;

        // Unknown: B does nothing, and says where to choose.
        profile.GamepadLayout = "auto";
        GamepadInput.ForgetLayouts();
        targets.SetTargeting(CursorTarget.Object, 0, TargetType.Neutral);
        await Button(host, JoyButton.B);
        Check("an unknown pad's B does not cancel", targets.IsTargeting);
        targets.CancelTarget();

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

        // A takes one where the pointer is: on the character.
        Vector2? character = await InputProbe.FindCharacter(host);

        if (character == null)
        {
            Check("the character is on screen for A", false);
        }
        else
        {
            GUO.Input.GodotInput.Handle(new InputEventMouseMotion { Position = character.Value });
            bool taken = false;
            targets.SetTargeting(o => taken = o != null, 0, TargetType.Neutral);
            await Button(host, JoyButton.A);
            Check("A takes a target under the pointer", taken && !targets.IsTargeting);
        }

        // Y taps the command bar's handle: one row opens to two, and back.
        // The bar is there only with the touch layer on (a device).
        var bar = GUO.Input.Touch.TouchInput.Bar;

        if (bar != null && bar.HandleShown)
        {
            int rows = bar.RowsOpen;
            await Button(host, JoyButton.Y);
            await InputProbe.Wait(host, 30);
            int opened = bar.RowsOpen;
            await Button(host, JoyButton.Y);
            await InputProbe.Wait(host, 30);
            Check("Y opens and closes the command bar's rows, as the handle does",
                opened != rows && bar.RowsOpen == rows, $"{rows} -> {opened} -> {bar.RowsOpen}");
        }
        else
        {
            GD.Print("[GUO] gamepad check: skip Y on the command bar (no touch bar, or its rows are off)");
        }

        profile.GamepadLayout = manual;
        GamepadInput.ForgetLayouts();
        Passed = _failed == 0;
        GD.Print($"[GUO] gamepad probe: {(Passed ? "PASS" : $"FAIL ({_failed})")}");
    }

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
