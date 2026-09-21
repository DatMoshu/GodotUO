// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Drives the running client with synthesised input and leaves a screenshot
/// behind, so "input works" is a picture rather than an assertion.
/// </summary>
/// <remarks>
/// The chain this exercises is the whole one: Godot delivers the event,
/// GameController._Input hands it to GodotInput, GodotInput translates it into
/// the shape upstream's filter produced, and the ported UIManager and
/// LoginScene act on it. If the login screen ends up with text in the account
/// field, every link held.
///
/// The pointer is warped rather than faked, because Mouse.Update asks the
/// window where the pointer is -- as upstream's does -- and a click event with
/// no pointer behind it would land wherever the real mouse happened to be.
/// </remarks>
internal static class InputProbe
{
    /// <summary>
    /// The account-name field of the login gump, in client pixels. Read off
    /// the login screen; it is fixed art, so it does not move.
    /// </summary>
    private static readonly Vector2 AccountField = new(180, 148);

    private const string TypeThis = "GUO";

    public static async void Run(Node host, int settleFrames)
    {
        await Frames(host, settleFrames);

        GD.Print("[GUO] input probe: clicking the account field");

        Godot.Input.WarpMouse(AccountField);
        await Frames(host, 2);

        Send(new InputEventMouseMotion { Position = AccountField });
        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = AccountField,
            Pressed = true,
        });
        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = AccountField,
            Pressed = false,
        });
        await Frames(host, 4);

        GD.Print($"[GUO] input probe: typing \"{TypeThis}\"");

        foreach (char c in TypeThis)
        {
            // Keycode is the capital because that is how Godot names a letter
            // key; Unicode is what the keypress produces, and is what the
            // client's text fields read.
            Send(new InputEventKey
            {
                Keycode = (Key)char.ToUpperInvariant(c),
                Unicode = c,
                Pressed = true,
            });
            await Frames(host, 2);

            Send(new InputEventKey
            {
                Keycode = (Key)char.ToUpperInvariant(c),
                Unicode = c,
                Pressed = false,
            });
            await Frames(host, 2);
        }

        await Frames(host, 10);
    }

    private static void Send(InputEvent e)
    {
        // Straight into the same queue a real device feeds, so nothing on the
        // path from the window to the client is bypassed.
        Godot.Input.ParseInputEvent(e);
    }

    private static async System.Threading.Tasks.Task Frames(Node host, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
