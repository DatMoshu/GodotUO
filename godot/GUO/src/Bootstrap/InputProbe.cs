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
/// Every event carries its own position and nothing touches the real pointer,
/// so a probe run does not fight whoever is using the machine.
/// </remarks>
internal static class InputProbe
{
    /// <summary>
    /// The account-name field of the login gump, in client pixels. Read off
    /// the login screen; it is fixed art, so it does not move.
    /// </summary>
    private static readonly Vector2 AccountField = new(320, 297);

    /// <summary>The password field, one row below.</summary>
    private static readonly Vector2 PasswordField = new(320, 340);

    /// <summary>
    /// The Login button. `LoginGump` builds two layouts and this client takes
    /// the 7.0.64 one, which puts Buttons.NextArrow at 280,365 -- not at the
    /// 610,445 the older layout uses. An earlier guess of 325,378 clipped the
    /// arrow's corner, which is why a run would sometimes log in and sometimes
    /// sit on the login screen.
    /// </summary>
    private static readonly Vector2 LoginButton = new(295, 378);

    /// <summary>The first row of the shard list.</summary>
    private static readonly Vector2 ShardEntry = new(250, 120);

    /// <summary>
    /// The forward arrow every character-creation page puts at 610,445.
    /// Coordinates here come from the gump classes, not from a screenshot:
    /// CreateCharAppearanceGump and CreateCharSelectionCityGump both place it
    /// there, so it is the same button all the way through.
    /// </summary>
    private static readonly Vector2 NextArrow = new(614, 449);

    /// <summary>
    /// The name field on the appearance page. `CreateCharAppearanceGump` puts
    /// the box at 257,65, 200x20.
    /// </summary>
    private static readonly Vector2 CharacterName = new(355, 75);

    /// <summary>
    /// The first profession tile. `CreateCharProfessionGump` lays them out at
    /// 145 + column * 195, 168 + row * 70. Any preset profession fills in the
    /// stats and skills and skips the trade page, which is what the probe
    /// wants: it is proving the world loads, not choosing a build.
    /// </summary>
    private static readonly Vector2 FirstProfession = new(200, 185);

    /// <summary>
    /// The credentials the probe logs in with. A dev shard with auto account
    /// creation on makes the account on first use, so these are not secrets
    /// and are not read from anywhere: they identify the probe's own account
    /// and nothing else.
    /// </summary>
    private const string ProbeAccount = "guoprobe";

    private const string ProbePassword = "guoprobe";

    private const string ProbeCharacter = "Guoprobe";

    public static async void Run(Node host, int settleFrames)
    {
        await Frames(host, settleFrames);

        GD.Print("[GUO] input probe: typing the account");

        await Click(host, AccountField);
        await Type(host, ProbeAccount);

        await Click(host, PasswordField);
        await Type(host, ProbePassword);

        GD.Print("[GUO] input probe: clicking Login");

        await Click(host, LoginButton);

        // The handshake, then the shard list.
        await Frames(host, 120);

        GD.Print("[GUO] input probe: selecting the shard");

        // One click. A fresh account has no characters, so the client walks
        // straight past the character list into creation -- there is no second
        // screen to accept here, and clicking as though there were pressed the
        // creation page's forward arrow with the name still empty.
        await Click(host, ShardEntry);

        await Frames(host, 180);

        GD.Print("[GUO] input probe: naming the character");

        await Click(host, CharacterName);
        await Type(host, ProbeCharacter);
        await Click(host, NextArrow);

        await Frames(host, 120);

        GD.Print("[GUO] input probe: choosing a profession");

        await Click(host, FirstProfession);

        await Frames(host, 120);

        GD.Print("[GUO] input probe: accepting the starting city");

        await Click(host, NextArrow);

        await Frames(host, 180);

        GD.Print("[GUO] input probe: full-size game window");

        FullSizeGameWindow();

        await Frames(host, 120);
    }

    /// <summary>
    /// Tick "always use fullsize game window", the way the options gump does.
    /// </summary>
    /// <remarks>
    /// The client starts a new profile with the setting off, so the world
    /// renders into a 600x480 viewport gump in the corner of whatever window
    /// it has. This is the same sequence OptionsGump.Apply runs when the
    /// checkbox is ticked -- resize the viewport to the window, move it to
    /// -5,-5, record both on the profile -- so the profile is saved with the
    /// option on and a later play.bat run comes up full size.
    /// </remarks>
    private static void FullSizeGameWindow()
    {
        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;
        Game.UI.Gumps.WorldViewportGump viewport =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.WorldViewportGump>();

        if (profile == null || viewport == null)
        {
            GD.Print("[GUO] input probe: no world viewport; not in the world yet");

            return;
        }

        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;

        viewport.ResizeGameWindow(new Compat.Point(bounds.Width, bounds.Height));
        viewport.SetGameWindowPosition(new Compat.Point(-5, -5));

        profile.GameWindowPosition = viewport.Location;
        profile.GameWindowFullSize = true;

        GD.Print($"[GUO] input probe: game window now {bounds.Width}x{bounds.Height}");
    }

    private static async System.Threading.Tasks.Task Type(Node host, string text)
    {
        foreach (char c in text)
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
    }

    private static async System.Threading.Tasks.Task Click(Node host, Vector2 at)
    {
        // No WarpMouse: the client takes the position off the event now
        // (see Mouse.Update), so the probe does not have to move the real
        // pointer -- which it could not reliably do anyway while someone was
        // using the machine, and which is what made these runs flaky.
        Send(new InputEventMouseMotion { Position = at });
        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = at,
            Pressed = true,
        });

        // Six frames, not two: a click that changes the screen does so on the
        // press, and a release two frames later can land on whatever button
        // the next screen put under the pointer. A human is slower than that.
        await Frames(host, 6);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = at,
            Pressed = false,
        });

        // Well clear of Mouse.MOUSE_DELAY_DOUBLE_CLICK (350 ms). Two probe
        // clicks a quarter of a second apart are one double click as far as
        // the client is concerned, whatever they land on, and a double click
        // on a list entry does not mean what a single click means.
        await Frames(host, 40);

        // What the click actually hit. A probe that types into nothing looks
        // exactly like a client that ignores typing, and this is the line that
        // tells the two apart.
        GD.Print(
            $"[GUO] probe click at {at.X},{at.Y}: focus is "
            + (Game.Managers.UIManager.KeyboardFocusControl?.GetType().Name ?? "none")
            + $", mouse over {Game.Managers.UIManager.MouseOverControl?.GetType().Name ?? "none"}"
        );
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
