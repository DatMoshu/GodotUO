// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Scenes;
using GUO.Input.Gamepad;

namespace GUO.Pregame3D;

/// <summary>
/// <c>--pregame3d-probe</c>: the whole login driven by synthetic pad events
/// through the real input path (Input.ParseInputEvent → GameController →
/// GamepadInput → the diorama), with a screenshot at each step: the account
/// and password typed on the on-screen keyboard, Login, the server, the
/// character list, a look at character creation and back, then Play into
/// the world. Account and password: <c>--account</c>/<c>--password</c>, else
/// guoprobe/guoprobe. Exits 0 when it reached the world, 1 otherwise.
/// </summary>
internal static class Pregame3DProbe
{
    private const int Device = 99; // a stand-in pad, its layout known (labels)

    private static bool _started;
    private static int _failed;
    private static string _dir = "/tmp/pregame3d-shots";

    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Run();
    }

    private static Node Host => Client.Game;

    private static void Check(string what, bool ok, string detail = "")
    {
        GD.Print($"[GUO] pregame3d probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    private static string Arg(string name, string fallback)
    {
        string[] args = OS.GetCmdlineUserArgs();

        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return fallback;
    }

    private static async Task Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await Host.ToSignal(Host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task<bool> Until(Func<bool> condition, int budget)
    {
        for (int i = 0; i < budget; i++)
        {
            if (condition())
            {
                return true;
            }

            await Frames(1);
        }

        return condition();
    }

    private static async Task Shot(string name)
    {
        await Host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        Image frame = Host.GetViewport().GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        string path = System.IO.Path.Combine(_dir, $"pregame3d_{name}.png");
        frame.SavePng(path);
        GD.Print($"[GUO] pregame3d probe: shot {path}");
    }

    /// <summary>One press and release of a pad button, through the real event queue.</summary>
    private static async Task Press(JoyButton button)
    {
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { Device = Device, ButtonIndex = button, Pressed = true });
        await Frames(2);
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { Device = Device, ButtonIndex = button, Pressed = false });
        await Frames(2);
    }

    private static Task Press(PadCmd cmd) => Press(cmd switch
    {
        PadCmd.Up => JoyButton.DpadUp,
        PadCmd.Down => JoyButton.DpadDown,
        PadCmd.Left => JoyButton.DpadLeft,
        PadCmd.Right => JoyButton.DpadRight,
        PadCmd.A => JoyButton.A,
        PadCmd.B => JoyButton.B,
        PadCmd.X => JoyButton.X,
        PadCmd.Y => JoyButton.Y,
        PadCmd.Start => JoyButton.Start,
        PadCmd.LeftShoulder => JoyButton.LeftShoulder,
        _ => JoyButton.RightShoulder,
    });

    /// <summary>Types <paramref name="text"/> on the open on-screen keyboard, key by key with the D-pad and A.</summary>
    private static async Task Type(string text)
    {
        OnScreenKeyboard osk = PregameDiorama.Instance.Keyboard;

        // Clear what the field held: X deletes.
        for (int i = osk.TextValue.Length; i > 0; i--)
        {
            await Press(PadCmd.X);
        }

        foreach (char c in text)
        {
            if (osk.Native)
            {
                // The device's keyboard types real key events into the window.
                Godot.Input.ParseInputEvent(new InputEventKey { Unicode = c, Pressed = true });
                await Frames(2);
                Godot.Input.ParseInputEvent(new InputEventKey { Unicode = c, Pressed = false });
                await Frames(2);
                continue;
            }

            foreach (PadCmd cmd in osk.PathTo(c))
            {
                await Press(cmd);
            }
        }
    }

    private static LoginScene Login => Client.Game?.GetScene<LoginScene>();

    private static LoginSteps Step => Login?.CurrentLoginStep ?? LoginSteps.Main;

    private static async Task Run()
    {
        _dir = Arg("--screenshot-dir", _dir);
        string account = Arg("--account", "guoprobe");
        string password = Arg("--password", account);
        GamepadInput.KnownLayout(Device, GamepadLayout.Labels);

        try
        {
            await Probe(account, password);
        }
        catch (Exception ex)
        {
            Check("ran to the end", false, ex.ToString());
        }

        GD.Print($"[GUO] pregame3d probe: {(_failed == 0 ? "PASS" : $"FAIL ({_failed})")}");
        Host.GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private static async Task Probe(string account, string password)
    {
        // 1. The login step, the lid opened.
        Check("the diorama is up", await Until(() => PregameDiorama.Active && PregameDiorama.Instance.Stage is LoginStage, 600));
        await Frames(140);
        Check("the lid opened", PregameDiorama.Instance.Lid > 0.95f, $"lid {PregameDiorama.Instance.Lid:0.00}");
        await Shot("01_login");

        // Resized mid-login: the whole scene stays framed at each size.
        Vector2I start = DisplayServer.WindowGetSize();

        foreach (Vector2I size in new[] { new Vector2I(640, 480), new Vector2I(1920, 1080) })
        {
            // As a window manager would: the window and its root viewport together.
            Host.GetTree().Root.Size = size;
            await Frames(60);
            Vector2I got = DisplayServer.WindowGetSize();
            Vector2 vp = Host.GetViewport().GetVisibleRect().Size;
            Check($"resized to {size.X}x{size.Y}", got == size && (Vector2I) vp == size, $"window {got.X}x{got.Y}, root viewport {vp.X}x{vp.Y}");
            await Shot($"01_login_{size.X}x{size.Y}");
        }

        Host.GetTree().Root.Size = start;
        await Frames(30);

        if (Step != LoginSteps.Main)
        {
            Check("still at the login step (autologin is off)", false, Step.ToString());
        }
        else
        {
            // 2. The account on the keyboard: focus it (X), type, Done.
            await Press(PadCmd.X);
            Check("X opens the keyboard on the account", PregameDiorama.Instance.Keyboard.IsOpen);
            await Type(account);
            await Shot("02_keyboard_account");
            await Press(PadCmd.Start);
            Check("the account typed", LoginStage.AccountForProbe == account, $"\"{LoginStage.AccountForProbe}\"");

            // 3. Done moved focus to the password: A opens it.
            Check("focus moved to the password", PregameDiorama.Instance.Focus.Current == PregameDiorama.Instance.FieldPassword);
            await Press(PadCmd.A);
            Check("A opens the keyboard on the password", PregameDiorama.Instance.Keyboard.IsOpen);
            await Type(password);
            await Shot("03_keyboard_password");
            await Press(PadCmd.Start);
            Check("focus moved to Login", PregameDiorama.Instance.Focus.Current == PregameDiorama.Instance.LoginButton);
            await Frames(10);
            await Shot("04_login_focused");

            // 4. A on the Login button.
            await Press(PadCmd.A);
        }

        Check("login started", await Until(() => Step != LoginSteps.Main, 60), Step.ToString());
        await Frames(2);
        await Shot("05_connecting");

        // 5. Servers (unless the shard's single server was taken by autologin).
        bool servers = await Until(() => Step is LoginSteps.ServerSelection or LoginSteps.CharacterSelection or LoginSteps.CharacterCreation or LoginSteps.PopUpMessage, 900);
        Check("reached the server or character list", servers && Step != LoginSteps.PopUpMessage, $"{Step} {Login?.PopupMessage}");

        if (Step == LoginSteps.ServerSelection)
        {
            await Frames(60);
            await Shot("06_servers");
            Check("a scroll is focused", PregameDiorama.Instance.Focus.Current is Hotspot);
            await Press(PadCmd.A);
        }

        // 6. Characters.
        Check("reached character selection", await Until(() => Step is LoginSteps.CharacterSelection or LoginSteps.CharacterCreation, 900), Step.ToString());

        if (Step == LoginSteps.CharacterSelection)
        {
            await Frames(60);
            await Shot("07_characters");

            // 7. A look at creation (X = new), a few changes, and back (B).
            await Press(PadCmd.X);

            if (await Until(() => Step == LoginSteps.CharacterCreation, 60))
            {
                await Frames(60);
                await Shot("08_creation");
                await Press(PadCmd.Down); // gender
                await Press(PadCmd.Right);
                await Press(PadCmd.Down); // race or skin
                await Press(PadCmd.Down);
                await Press(PadCmd.Right);
                await Press(PadCmd.Right);
                await Frames(10);
                await Shot("09_creation_changed");

                // The name on the keyboard, then Next, a profession, the cities.
                for (int i = 0; i < 12; i++)
                {
                    await Press(PadCmd.Up);
                }

                await Press(PadCmd.A);
                Check("A on Name opens the keyboard", PregameDiorama.Instance.Keyboard.IsOpen);
                await Type("Pebble");
                await Press(PadCmd.Start);
                await Press(PadCmd.Start); // Next
                await Frames(10);
                await Shot("10_creation_profession");
                await Press(PadCmd.A); // the first profession (or category)
                await Frames(10);
                await Shot("11_creation_next");
                await Press(PadCmd.B);
                await Press(PadCmd.B);
                await Press(PadCmd.B);
                Check("B goes back to the characters", await Until(() => Step == LoginSteps.CharacterSelection, 60), Step.ToString());
                await Frames(40);
            }
            else
            {
                GD.Print("[GUO] pregame3d probe: no free slot, creation not visited");
            }

            // 8. Play the focused (last played) character.
            Check("a character is focused", PregameDiorama.Instance?.Focus.Current is Hotspot);
            await Press(PadCmd.A);
        }
        else
        {
            Check("the account has a character to play", false, "creation came up instead");
            return;
        }

        bool inWorld = await Until(() => Client.Game?.UO?.World?.InGame ?? false, 1200);
        Check("entered the world", inWorld);
        await Frames(90);
        Check("the diorama left with the login scene", !PregameDiorama.Active);
        await Shot("12_world");
    }
}
