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
/// GamepadInput → the pregame), with a screenshot at each step: the account
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
        OnScreenKeyboard osk = PregameScreen.Instance.Keyboard;

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
        // A fresh account (the dev shard makes it on first login) has no
        // character, so the run goes through the whole of creation; an
        // existing one (--account guoprobe) plays its character instead.
        string account = Arg("--account", "p3d" + DateTime.Now.ToString("MMddHHmmss"));
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
        Check("the pregame is up", await Until(() => PregameScreen.Active && PregameScreen.Instance.Stage is LoginStage, 600));
        await Frames(140);
        Check("the painting is up", PregameScreen.Instance.Painted);
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
            Check("X opens the keyboard on the account", PregameScreen.Instance.Keyboard.IsOpen);
            await Type(account);
            await Shot("02_keyboard_account");
            await Press(PadCmd.Start);
            Check("the account typed", LoginStage.AccountForProbe == account, $"\"{LoginStage.AccountForProbe}\"");

            // 3. Done moved focus to the password: A opens it.
            Check("focus moved to the password", LoginStage.ProbeOnPassword);
            await Press(PadCmd.A);
            Check("A opens the keyboard on the password", PregameScreen.Instance.Keyboard.IsOpen);
            await Type(password);
            await Shot("03_keyboard_password");
            await Press(PadCmd.Start);
            Check("focus moved to Login", LoginStage.ProbeOnLogin);
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
            Check("a server is focused", ServerStage.ProbeServerFocused);
            await Press(PadCmd.A);
        }

        // 6. Characters.
        Check("reached character selection", await Until(() => Step is LoginSteps.CharacterSelection or LoginSteps.CharacterCreation, 900), Step.ToString());

        if (Step == LoginSteps.CharacterSelection)
        {
            await Frames(60);
            await Shot("07_characters");

            // A look at creation (X = new) and back (B).
            await Press(PadCmd.X);

            if (await Until(() => Step == LoginSteps.CharacterCreation, 60))
            {
                await Frames(60);
                await Shot("08_creation_trade");
                await Press(PadCmd.B);
                Check("B goes back to the characters", await Until(() => Step == LoginSteps.CharacterSelection, 60), Step.ToString());
                await Frames(40);
            }

            Check("a character is focused", CharacterStage.ProbeCharacterFocused);
            await Press(PadCmd.A);
        }
        else
        {
            await Creation();
        }

        bool inWorld = await Until(() => Client.Game?.UO?.World?.InGame ?? false, 1200);
        Check("entered the world", inWorld);
        await Frames(90);
        Check("the pregame left with the login scene", !PregameScreen.Active);
        await Shot("12_world");
    }

    private static string Tag => CreationStage.ProbeFocusTag ?? "";

    /// <summary>Down (then up) a list until the focus's tag matches.</summary>
    private static async Task<bool> SeekList(Func<string, bool> match)
    {
        foreach (PadCmd dir in new[] { PadCmd.Down, PadCmd.Up })
        {
            for (int i = 0; i < 40; i++)
            {
                if (match(Tag))
                {
                    return true;
                }

                string before = Tag;
                await Press(dir);

                if (Tag == before)
                {
                    break;
                }
            }
        }

        return match(Tag);
    }

    /// <summary>Row by row across a grid until the focus's tag matches.</summary>
    private static async Task<bool> SeekGrid(Func<string, bool> match)
    {
        for (int i = 0; i < 6; i++)
        {
            await Press(PadCmd.Up);
            await Press(PadCmd.Left);
            await Press(PadCmd.Left);
            await Press(PadCmd.Left);
        }

        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                if (match(Tag))
                {
                    return true;
                }

                string before = Tag;
                await Press(PadCmd.Right);

                if (Tag == before)
                {
                    break;
                }
            }

            if (match(Tag))
            {
                return true;
            }

            await Press(PadCmd.Down);

            for (int i = 0; i < 6; i++)
            {
                await Press(PadCmd.Left);
            }
        }

        return match(Tag);
    }

    private static async Task Stick(float x)
    {
        Godot.Input.ParseInputEvent(new InputEventJoypadMotion { Device = Device, Axis = JoyAxis.RightX, AxisValue = x });
        await Frames(3);
        Godot.Input.ParseInputEvent(new InputEventJoypadMotion { Device = Device, Axis = JoyAxis.RightX, AxisValue = 0f });
        await Frames(3);
    }

    /// <summary>Every step of the Tailor's Table, an Advanced character, then Enter Britannia.</summary>
    private static async Task Creation()
    {
        Check("creation came up (a fresh account)", await Until(() => CreationStage.ProbeStep == CreationStage.Step.Trade, 120));
        await Frames(60);
        await Shot("c1_trade");

        // 1 Trade: Advanced.
        Check("found the Advanced card", await SeekGrid(t => t.Contains("advanced")), Tag);
        await Frames(5);
        await Shot("c1b_trade_advanced");
        await Press(PadCmd.A);
        Check("A on a card goes to Look", await Until(() => CreationStage.ProbeStep == CreationStage.Step.Look, 30));

        // 2 Look: turn the figure, a hair style, a shirt colour from the palette.
        await Frames(20);
        byte? d0 = CreationStage.ProbeDirection;
        await Stick(1f);
        await Stick(1f);
        Check("the right stick turns the figure", CreationStage.ProbeDirection != d0, $"{d0} -> {CreationStage.ProbeDirection}");
        await SeekList(t => t == "hair");
        await Press(PadCmd.Right);
        await Press(PadCmd.Right);
        Check("found the Shirt row", await SeekList(t => t == "shirt"), Tag);
        await Press(PadCmd.A);
        Check("A on a colour opens the palette", CreationStage.ProbePopoverOpen);

        for (int i = 0; i < 5; i++)
        {
            await Press(PadCmd.Right);
        }

        await Press(PadCmd.Down);
        await Press(PadCmd.Down);
        await Frames(10);
        await Shot("c2_palette");
        await Press(PadCmd.A);
        Check("A keeps the colour", !CreationStage.ProbePopoverOpen);
        await Frames(10);
        await Shot("c3_look");

        // 3 Skills: more Str, four skills, a changed value.
        await Press(PadCmd.Start);
        Check("Start goes to Skills (Advanced)", await Until(() => CreationStage.ProbeStep == CreationStage.Step.Skills, 30));
        await SeekList(t => t == "stat:str");

        for (int i = 0; i < 8; i++)
        {
            await Press(PadCmd.Left); // Str starts at its cap: down, the others up
        }

        await SeekList(t => t == "stat:int");

        for (int i = 0; i < 3; i++)
        {
            await Press(PadCmd.Right);
        }

        for (int slot = 0; slot < 4; slot++)
        {
            if (!await SeekList(t => t == $"slot:{slot}"))
            {
                break;
            }

            await Press(PadCmd.A);

            for (int i = 0; i < 2 + slot * 3; i++)
            {
                await Press(PadCmd.Down);
            }

            if (slot == 1)
            {
                await Frames(5);
                await Shot("c4_skill_list");
            }

            await Press(PadCmd.A);
            await Frames(4);
        }

        await SeekList(t => t == "slot:0");

        for (int i = 0; i < 6; i++)
        {
            await Press(PadCmd.Right);
        }

        await Frames(10);
        await Shot("c5_skills");
        await Press(PadCmd.Start);
        Check("Start goes to Home", await Until(() => CreationStage.ProbeStep == CreationStage.Step.Home, 30), CreationStage.ProbeStep?.ToString());

        // 4 Home: a city that is not the first offered.
        Check("the map was drawn", await Until(() => CreationStage.ProbeMapReady, 600));
        await Frames(10);
        string first = Tag;

        foreach (PadCmd dir in new[] { PadCmd.Right, PadCmd.Down, PadCmd.Left, PadCmd.Up })
        {
            if (Tag != first)
            {
                break;
            }

            await Press(dir);
        }

        Check("the D-pad moved to another city", Tag != first && Tag.StartsWith("city:"), $"{first} -> {Tag}");
        await Frames(5);
        await Shot("c6_home");
        await Press(PadCmd.A);
        Check("A on a city goes to Name", await Until(() => CreationStage.ProbeStep == CreationStage.Step.Name, 30));

        // 5 Name, then Enter Britannia.
        await Press(PadCmd.A);
        Check("A on the name opens the keyboard", PregameScreen.Instance.Keyboard.IsOpen);
        await Type("Pebble");
        await Press(PadCmd.Start);
        await Frames(10);
        await Shot("c7_name");
        await Press(PadCmd.Start);
        Check("Enter Britannia created the character", await Until(() => Step is LoginSteps.CharacterCreationDone or LoginSteps.EnteringBritania || (Client.Game?.UO?.World?.InGame ?? false), 120), Step.ToString());
    }
}
