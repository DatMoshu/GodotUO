// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Scenes;
using GUO.Configuration;
using GUO.Input.Gamepad;
using GUO.Input.Touch.Pregame;

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
        AssertOnScreen(name);
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

        lock (PregameAssets.Timings)
        {
            foreach (var kv in PregameAssets.Timings)
            {
                GD.Print($"[GUO] pregame3d probe: timing {kv.Key}: {kv.Value} ms");
            }
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

        // The Deck's case first: the OS window grows (to 1280x800) while the
        // root viewport is left at the project's 1280x720. The pregame must
        // notice, resize the root, and fill the whole window.
        // (Started at the project's own 1280x720, as the Deck is.)
        Vector2I start = DisplayServer.WindowGetSize();

        if (start == new Vector2I(1280, 720))
        {
            var deck = new Vector2I(1280, 800);
            DisplayServer.WindowSetSize(deck);
            await Frames(60);
            Vector2I rootNow = Host.GetTree().Root.Size;
            Check("window and root viewport agree after an OS-side resize", DisplayServer.WindowGetSize() == deck && rootNow == deck, $"window {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}, root {rootNow.X}x{rootNow.Y}");
            await Shot("01_login_deck_mismatch");
            start = deck;
        }

        // Resized mid-login: the whole scene stays framed at each size.
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
            // 1b. The server list, by pad: the entry beside Quit, the card, a pick and the way back.
            await ServersChecks();

            // 2. The account on the keyboard: focus it (X), type, Done.
            await Press(PadCmd.X);
            Check("X opens the keyboard on the account", PregameScreen.Instance.Keyboard.IsOpen);
            AssertFieldOnTop("account", LoginStage.ProbeAccountField);
            await Type(account);
            await Shot("02_keyboard_account");
            await Press(PadCmd.Start);
            Check("the account typed", LoginStage.AccountForProbe == account, $"\"{LoginStage.AccountForProbe}\"");

            // 3. Done moved focus to the password: A opens it.
            Check("focus moved to the password", LoginStage.ProbeOnPassword);
            await Press(PadCmd.A);
            Check("A opens the keyboard on the password", PregameScreen.Instance.Keyboard.IsOpen);
            AssertFieldOnTop("password", LoginStage.ProbePasswordField);
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

            // One account for every run (the shard allows few per IP): the
            // probe's own character from the last run is deleted first, with
            // the gump's Delete and its question. The dev shard's first
            // account (guoprobe) is its owner, so a young character may go.
            for (int tries = 0; tries < 3 && HasProbeCharacter(); tries++)
            {
                if (!await SeekList(t => t.Equals("char:" + ProbeName, StringComparison.OrdinalIgnoreCase)))
                {
                    Check("found last run's character to delete", false, Tag);
                    break;
                }

                await Press(PadCmd.Y);
                await Frames(5);
                await Shot("07b_delete_question");
                await Press(PadCmd.A);
                bool gone = await Until(() => !HasProbeCharacter() || !string.IsNullOrEmpty(Login?.PopupMessage) || PregameScreen.Instance.ModalOpen, 600);
                await Frames(30);
                Check("last run's character deleted", !HasProbeCharacter(), Login?.PopupMessage ?? "");

                if (PregameScreen.Instance.ModalOpen)
                {
                    await Press(PadCmd.A);
                }

                if (!gone)
                {
                    break;
                }
            }

            await Frames(30);
            await Press(PadCmd.X);
            Check("X opens creation", await Until(() => Step == LoginSteps.CharacterCreation, 120), Step.ToString());
        }

        if (Step == LoginSteps.CharacterCreation)
        {
            await Creation();
        }

        bool inWorld = await Until(() => Client.Game?.UO?.World?.InGame ?? false, 1200);
        Check("entered the world", inWorld);
        await Frames(90);
        Check("the pregame left with the login scene", !PregameScreen.Active);
        await Shot("12_world");
    }

    /// <summary>Walks the card's pad cursor (Down, Right, Up, Left in turn) until <paramref name="match"/> holds.</summary>
    private static async Task<bool> PadTo(GUO.Input.Touch.Pregame.PregameCard card, Func<Control, bool> match, bool sideways = false)
    {
        foreach (PadCmd dir in sideways ? new[] { PadCmd.Right, PadCmd.Up, PadCmd.Down, PadCmd.Left } : new[] { PadCmd.Down, PadCmd.Right, PadCmd.Up, PadCmd.Left })
        {
            for (int i = 0; i < 40; i++)
            {
                Control o = card.PadFocusOwner;

                if (o != null && match(o))
                {
                    return true;
                }

                await Press(dir);

                if (o != null && card.PadFocusOwner == o)
                {
                    break;
                }
            }
        }

        Control last = card.PadFocusOwner;

        return last != null && match(last);
    }

    /// <summary>
    /// The 3D login's Servers entry (PR #16 review): reachable by the D-pad, LB too; it opens the
    /// classic server card, which the pad drives; a pick (Play) comes back to the login step with
    /// the server set; a server that needs its own client files asks the classic restart question
    /// (answered No with B); B closes the card.
    /// </summary>
    private static async Task ServersChecks()
    {
        Check("the server card is built", await Until(() => GUO.Input.Touch.Pregame.PregameCard.Instance != null, 600));
        GUO.Input.Touch.Pregame.PregameCard card = GUO.Input.Touch.Pregame.PregameCard.Instance;

        if (card == null)
        {
            return;
        }

        // The D-pad from the password field: Left to Quit, Up to Servers; A opens the card.
        await Press(PadCmd.Left);
        await Press(PadCmd.Up);
        Check("the D-pad reaches the Servers entry", LoginStage.ProbeOnServers, Tag);
        await Shot("01b_servers_focus");
        await Press(PadCmd.A);
        Check("A on Servers opens the card over the login", await Until(() => LoginStage.ProbeCardOpen, 30));
        await Frames(30);
        await Shot("01c_servers_card");
        await Press(PadCmd.B);
        Check("B closes the card", await Until(() => !LoginStage.ProbeCardOpen, 30));

        // LB opens it too; a pick of the dev shard, then Play.
        ServerEntry dev = ServerBook.DevEntry;
        await Press(PadCmd.LeftShoulder);
        Check("LB opens the card", await Until(() => LoginStage.ProbeCardOpen, 30));
        await Frames(10);
        PregameServers servers = card.Servers;
        Control row = dev == null ? null : servers.RowFor(dev);
        Check("the dev shard is listed", row != null);

        if (row != null)
        {
            Check("the pad reaches the server's row", await PadTo(card, c => c == servers.RowFor(dev)));
            await Press(PadCmd.A);
            await Frames(10);
            Check("A selects it", servers.Selected == dev);
            Check("the pad reaches Play", await PadTo(card, c => servers.PlayButton != null && c == servers.PlayButton, sideways: true));
            await Press(PadCmd.A);
            Check("Play comes back to the login step with the server set",
                await Until(() => !LoginStage.ProbeCardOpen, 60) && Step == LoginSteps.Main
                && Settings.GlobalSettings.Port == dev.Port && ServerPlay.LastOutcome.Contains("log in to"),
                ServerPlay.LastOutcome);
        }
        else
        {
            await Press(PadCmd.B);
        }

        await Frames(20);
        await Shot("01d_servers_back");

        // A server that needs its own client files: Play asks the classic question, B answers No.
        ServerEntry files = ServerBook.Add("Probe Files", "127.0.0.1", "2598", out _);

        try
        {
            files.NeedsCustomData = true;
            files.DataFolder = System.Environment.GetEnvironmentVariable("UO_CLIENT_DATA") ?? "";
            ServerBook.Save();
            await Press(PadCmd.LeftShoulder);
            await Until(() => LoginStage.ProbeCardOpen, 30);
            servers.Rebuild();
            await Frames(10);
            Check("the pad reaches the files server's row", await PadTo(card, c => servers.RowFor(files) != null && c == servers.RowFor(files)));
            await Press(PadCmd.A);
            await Frames(10);
            Check("the pad reaches Play on it", await PadTo(card, c => servers.PlayButton != null && c == servers.PlayButton, sideways: true), card.PadFocusOwner?.GetType().Name + " " + card.PadFocusOwner?.Name + " play " + (servers.PlayButton?.Disabled));
            await Press(PadCmd.A);
            Check("Play asks to restart with the shard's files (nothing is restarted)",
                await Until(() => servers.ConfirmButton != null && servers.DetailText.Contains("Restart GUO with Probe Files"), 60), servers.DetailText);
            await Frames(10);
            await Shot("01e_servers_files_question");
            await Press(PadCmd.B);
            Check("B answers No", await Until(() => servers.ConfirmButton == null, 30) && LoginStage.ProbeCardOpen);
            await Press(PadCmd.B);
            Check("B closes the card", await Until(() => !LoginStage.ProbeCardOpen, 30));
        }
        finally
        {
            GUO.Input.Touch.Pregame.PregameCard.CloseOnMain();
            ServerEntry left = ServerBook.Find("127.0.0.1", 2598);

            if (left != null)
            {
                ServerBook.Remove(left);
            }
        }

        await Frames(20);
    }

    private const string ProbeName = "Pebble";

    private static bool HasProbeCharacter() => Array.Exists(Login?.Characters ?? Array.Empty<string>(), c => string.Equals(c, ProbeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The focused thing's tag, on any step.</summary>
    private static string Tag => PregameScreen.Instance?.Focus.Current switch
    {
        UiFocus u => u.Tag as string ?? "",
        GumpProp g => g.Tag ?? "",
        _ => "",
    };

    /// <summary>
    /// The field being typed into is the field itself (not a card), on
    /// screen, and nothing drawn after it covers it.
    /// </summary>
    private static void AssertFieldOnTop(string what, Control field)
    {
        OnScreenKeyboard osk = PregameScreen.Instance?.Keyboard;
        bool inPlace = osk != null && field != null && ReferenceEquals(osk.EditedField, field);
        Check($"{what}: typed in place in the field", inPlace, field == null ? "no field" : "the keyboard edits another control");

        if (field == null || !field.IsVisibleInTree())
        {
            Check($"{what}: the field is visible", false);
            return;
        }

        Rect2 r = field.GetGlobalRect();
        Vector2I root = Host.GetTree().Root.Size;
        bool onScreen = new Rect2(Vector2.Zero, root).Grow(6f).Encloses(r);
        var over = new System.Collections.Generic.List<string>();
        bool after = false;

        void Walk(Node n)
        {
            if (n is Control c)
            {
                if (!c.IsVisibleInTree())
                {
                    return;
                }

                if (ReferenceEquals(c, field))
                {
                    after = true;
                    return; // its own children are its text
                }

                bool drawsSomething = c is Panel or PanelContainer or TextureRect or ColorRect || (c is Label l && l.Text.Length > 0);

                if (after && drawsSomething && !c.IsAncestorOf(field))
                {
                    Rect2 o = c.GetGlobalRect().Intersection(r);

                    if (o.Size.X > 2 && o.Size.Y > 2)
                    {
                        over.Add($"{c.GetType().Name} \"{(c as Label)?.Text ?? c.Name}\"");
                    }
                }
            }

            foreach (Node child in n.GetChildren())
            {
                Walk(child);
            }
        }

        Walk(PregameScreen.Instance.Props);
        Walk(PregameScreen.Instance.OverlayRoot);
        Check($"{what}: the field is on screen and nothing covers it (card shown: {osk?.CardShown})", onScreen && over.Count == 0,
            $"at {r.Position.X:0},{r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0}; covered by {string.Join(", ", over)}");
    }

    /// <summary>
    /// Every visible control of the pregame inside the root viewport (a
    /// scroll's rows are its own business); fails loudly with the offenders.
    /// </summary>
    private static void AssertOnScreen(string where)
    {
        if (!PregameScreen.Active)
        {
            return;
        }

        Vector2I root = Host.GetTree().Root.Size;
        Rect2 screen = new Rect2(Vector2.Zero, root).Grow(6f); // the drift moves the painting's pieces a few pixels
        var bad = new System.Collections.Generic.List<string>();

        void Walk(Node n, bool clipped)
        {
            if (n is Control c)
            {
                if (!c.IsVisibleInTree())
                {
                    return;
                }

                Rect2 r = c.GetGlobalRect();

                if (!clipped && r.Size.X > 0 && r.Size.Y > 0 && !screen.Encloses(r))
                {
                    bad.Add($"{c.GetType().Name} \"{(c as Label)?.Text ?? c.Name}\" at {r.Position.X:0},{r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0}");
                }

                clipped |= c is ScrollContainer;
            }

            foreach (Node child in n.GetChildren())
            {
                Walk(child, clipped);
            }
        }

        Walk(PregameScreen.Instance.OverlayRoot, false);
        Walk(PregameScreen.Instance.Props, false);
        Check($"{where}: everything inside the {root.X}x{root.Y} screen", bad.Count == 0, string.Join("; ", bad.GetRange(0, Math.Min(6, bad.Count))));
        int standIns = Overlay.CountStandIns(PregameScreen.Instance.OverlayRoot) + Overlay.CountStandIns(PregameScreen.Instance.Props);
        Check($"{where}: no flat stand-in frames, only the client's art", standIns == 0, $"{standIns} stand-in(s)");
    }

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
        AssertFieldOnTop("name", CreationStage.ProbeNameField);
        await Type("Pebble");
        await Press(PadCmd.Start);
        await Frames(10);
        await Shot("c7_name");
        await Press(PadCmd.Start);
        Check("Enter Britannia created the character", await Until(() => Step is LoginSteps.CharacterCreationDone or LoginSteps.EnteringBritania || (Client.Game?.UO?.World?.InGame ?? false), 120), Step.ToString());
    }
}
