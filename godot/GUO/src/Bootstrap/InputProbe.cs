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

        await Click(host, ShardEntry);

        await Frames(host, 180);

        // What comes next depends on the account, not on the probe. The first
        // run makes the character and the client goes straight into creation;
        // every run after that stops at the character list with that same
        // character on it. Reading which gump is up is the only way to know:
        // walking the creation pages blind on a returning account pressed the
        // list's forward arrow with nothing chosen, and the probe sat at a
        // loading screen for the rest of the run.
        if (Game.Managers.UIManager.GetGump<Game.UI.Gumps.Login.CharacterSelectionGump>() != null)
        {
            GD.Print("[GUO] input probe: the account has a character; logging it in");

            // CharacterSelectionGump.Buttons: Next is 2, and it logs in
            // whichever character is selected, which is the first by default.
            await ClickGumpButton<Game.UI.Gumps.Login.CharacterSelectionGump>(host, 2, "Next");
        }
        else
        {
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
        }

        await Frames(host, 240);

        GD.Print("[GUO] input probe: full-size game window");

        FullSizeGameWindow();

        await Frames(host, 120);

        GD.Print("[GUO] input probe: walking");

        // A step plays a footstep, so this is where a sound effect can be
        // listened for. It has to be caught while it is playing: an effect is
        // about a second long, and by the end of the run there is nothing
        // left to hear but the music, which says nothing about UOSound.
        await ListenForSound(host, Walk(host));

        await Frames(host, 60);

        GD.Print("[GUO] input probe: opening the backpack");

        await ExpandTopBar(host);

        await OpenInventory(host);

        // Before anything else is opened. A gump that opens later sits on top
        // of the backpack, and a press lands on whatever is topmost -- the
        // drag then moves that gump instead of the item, which is what
        // happened when this ran after the skills list and the maps.
        GD.Print("[GUO] input probe: moving an item in the backpack");

        await MoveItemInBackpack(host);

        GD.Print("[GUO] input probe: skills and status");

        // PaperDollGump.Buttons: Skills is 5, Status is 8.
        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 5, "Skills");
        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 8, "Status");

        GD.Print("[GUO] input probe: the maps");

        // TopBarGump.Buttons: Map is 0 and WorldMap is 6. Both are worth
        // opening because they are the only two things that draw a map rather
        // than the world: the minimap builds one texture out of MultiMap.mul,
        // and the world map decodes a PNG and draws it itself.
        await ClickGumpButton<Game.UI.Gumps.TopBarGump>(host, 0, "Map");
        Report<Game.UI.Gumps.MiniMapGump>("minimap");

        await ClickGumpButton<Game.UI.Gumps.TopBarGump>(host, 6, "World Map");
        Report<Game.UI.Gumps.WorldMapGump>("world map");

        GD.Print("[GUO] input probe: double-clicking the character");

        await DoubleClickSelf(host);

        await MeasureFrames(host, 300);

        GD.Print("[GUO] input probe: speaking");

        await Speak(host, "hail from godot");

        await Frames(host, 90);

        // What is on screen at the end, named. A gump that opened behind
        // another is still a gump that opened, and a screenshot cannot say so.
        var open = new System.Collections.Generic.List<string>();

        foreach (Game.UI.Gumps.Gump g in Game.Managers.UIManager.Gumps)
        {
            if (g.IsVisible && !g.IsDisposed)
            {
                open.Add(g.GetType().Name);
            }
        }

        GD.Print($"[GUO] input probe: gumps open: {string.Join(", ", open)}");
        GD.Print($"[GUO] input probe: audio: {Client.Game.Audio.NowPlaying}");
    }

    /// <summary>
    /// Put the top bar on its expanded page, where its buttons are.
    /// </summary>
    /// <remarks>
    /// The bar remembers which page it was on in the profile, so a run that
    /// collapsed it leaves every later run starting with nothing to click.
    /// </remarks>
    private static async System.Threading.Tasks.Task ExpandTopBar(Node host)
    {
        Game.UI.Gumps.TopBarGump bar =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.TopBarGump>();

        if (bar == null || bar.ActivePage == 1)
        {
            return;
        }

        Game.UI.Controls.Button arrow = FindPageButton(bar, 1);

        if (arrow == null)
        {
            GD.Print($"[GUO] input probe: top bar is on page {bar.ActivePage} and will not open");

            return;
        }

        GD.Print("[GUO] input probe: the top bar was collapsed; opening it");

        await Click(
            host,
            new Vector2(
                arrow.ScreenCoordinateX + arrow.Width / 2f,
                arrow.ScreenCoordinateY + arrow.Height / 2f
            )
        );

        await Frames(host, 60);
    }

    private static Game.UI.Controls.Button FindPageButton(
        Game.UI.Controls.Control parent,
        int toPage
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.Page != 0 && child.Page != parent.ActivePage)
            {
                continue;
            }

            if (child is Game.UI.Controls.Button b && b.ToPage == toPage)
            {
                return b;
            }

            Game.UI.Controls.Button found = FindPageButton(child, toPage);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Press Inventory on the top bar, which asks the server for the backpack.
    /// </summary>
    /// <remarks>
    /// The button is found rather than guessed at: `TopBarGump` lays its
    /// buttons out from the widths of the gump art it loads, so their
    /// positions are not in the source to read off. The click itself still
    /// goes through Godot and the input layer like any other.
    ///
    /// A container is a round trip and then some -- the client asks, the
    /// server sends the container and every item in it, and the gump is built
    /// from art looked up per item.
    /// </remarks>
    private static async System.Threading.Tasks.Task OpenInventory(Node host)
    {
        Game.UI.Gumps.TopBarGump bar =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.TopBarGump>();

        Game.UI.Controls.Button button = bar == null ? null : FindButton(bar, 2);

        if (button == null)
        {
            GD.Print("[GUO] input probe: no Inventory button on the top bar");

            return;
        }

        await Click(
            host,
            new Vector2(
                button.ScreenCoordinateX + button.Width / 2f,
                button.ScreenCoordinateY + button.Height / 2f
            )
        );

        await Frames(host, 120);

        Game.UI.Gumps.ContainerGump pack =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

        GD.Print(
            pack == null
                ? "[GUO] input probe: no container gump"
                : $"[GUO] input probe: container gump {pack.Width}x{pack.Height} "
                  + $"with {pack.Children.Count} children"
        );
    }

    /// <summary>
    /// Find a button by its id inside a gump and click where it actually is.
    /// </summary>
    /// <remarks>
    /// Gumps lay themselves out from the size of the art they load, so a
    /// coordinate written here would be a guess. The click still goes through
    /// Godot and the input layer; only the target is looked up.
    /// </remarks>
    private static async System.Threading.Tasks.Task ClickGumpButton<T>(
        Node host,
        int buttonId,
        string what
    )
        where T : Game.UI.Gumps.Gump
    {
        T gump = Game.Managers.UIManager.GetGump<T>();
        Game.UI.Controls.Button button = gump == null ? null : FindButton(gump, buttonId);

        if (button == null)
        {
            GD.Print($"[GUO] input probe: no {what} button on {typeof(T).Name}");

            return;
        }

        await Click(
            host,
            new Vector2(
                button.ScreenCoordinateX + button.Width / 2f,
                button.ScreenCoordinateY + button.Height / 2f
            )
        );

        await Frames(host, 90);
    }

    /// <summary>
    /// Pick an item up out of the backpack and put it down somewhere else in
    /// the same container.
    /// </summary>
    /// <remarks>
    /// The first interaction the probe does that is not a click. Dragging is
    /// its own path through the client: the press arms the control, motion
    /// while the button is down is what turns it into a drag, the item comes
    /// off the server's copy of the container and onto the cursor, and the
    /// release sends a drop request that the server can refuse. Nothing in
    /// the click tests reaches any of it.
    ///
    /// The move is within the backpack on purpose. Dropping on the ground or
    /// on another container brings in reachability and weight rules, and a
    /// refusal there would say nothing about the client.
    /// </remarks>
    private static async System.Threading.Tasks.Task MoveItemInBackpack(Node host)
    {
        Game.UI.Gumps.ContainerGump pack =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

        Game.UI.Controls.ItemGump item = pack == null ? null : FindItem(pack);

        if (item == null)
        {
            GD.Print("[GUO] input probe: nothing in the backpack to move");

            return;
        }

        Vector2 from = GrabPoint(item);

        // Down and to the right, staying well inside the container art: the
        // drop point has to be over the container for the server to read it
        // as a move rather than a throw.
        Vector2 to = from + new Vector2(40, 30);

        GD.Print(
            $"[GUO] input probe: dragging 0x{item.LocalSerial:X} from "
            + $"{from.X},{from.Y} to {to.X},{to.Y}"
        );

        // Which item moved is read back from the press, not assumed. Items in
        // a container overlap, so the solid pixel aimed at can belong to the
        // item drawn over the one it was picked from, and then the drag is
        // still a real drag -- of a different item.
        Game.UI.Controls.Control pressed = await Drag(host, from, to);
        uint serial = pressed?.LocalSerial ?? item.LocalSerial;

        await Frames(host, 120);

        pack = Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

        Game.UI.Controls.ItemGump moved = pack == null ? null : FindItem(pack, serial);

        GD.Print(
            moved == null
                ? $"[GUO] input probe: 0x{serial:X} is no longer in the backpack"
                : $"[GUO] input probe: 0x{serial:X} is now at "
                  + $"{moved.ScreenCoordinateX},{moved.ScreenCoordinateY}"
        );

        GD.Print(
            "[GUO] input probe: the cursor is holding something: "
            + Client.Game.UO.GameCursor.ItemHold.Enabled
        );
    }

    /// <summary>
    /// Double-click the character in the world, which opens the paperdoll.
    /// </summary>
    /// <remarks>
    /// The only thing the probe does that picks an object out of the world
    /// rather than out of a gump. The click goes to the middle of the window,
    /// where the camera keeps the player, and what it lands on is decided by
    /// the renderer's own hit test against the drawn sprites -- so a paperdoll
    /// coming back is evidence that picking works, not just that the click
    /// arrived.
    /// </remarks>
    private static async System.Threading.Tasks.Task DoubleClickSelf(Node host)
    {
        Game.UI.Gumps.PaperDollGump paperdoll =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.PaperDollGump>();

        // Closed first, so that a paperdoll at the end means this opened it.
        paperdoll?.Dispose();

        await Frames(host, 30);

        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;
        var centre = new Vector2(bounds.Width / 2f, bounds.Height / 2f);

        // The camera keeps the player in the middle of the window, but the
        // middle of the window is a floor tile: a mobile is drawn standing on
        // its tile, so its body is above that point, and how far above depends
        // on the body. Rather than guess the offset, the probe moves the mouse
        // up the column and asks the client what is under it -- the same hit
        // test a player's own aiming relies on.
        Vector2? found = null;

        for (int dy = 0; dy >= -120 && found == null; dy -= 10)
        {
            var at = new Vector2(centre.X, centre.Y + dy);

            Send(new InputEventMouseMotion { Position = at });

            await Frames(host, 4);

            if (ReferenceEquals(Game.SelectedObject.Object, Client.Game.UO.World.Player))
            {
                found = at;
            }
        }

        if (found == null)
        {
            GD.Print("[GUO] input probe: could not find the character under the cursor");

            return;
        }

        GD.Print($"[GUO] input probe: the character is under {found.Value.X},{found.Value.Y}");

        await DoubleClick(host, found.Value);

        await Frames(host, 90);

        Report<Game.UI.Gumps.PaperDollGump>("paperdoll");
    }

    /// <summary>
    /// Two clicks inside <see cref="Input.Mouse.MOUSE_DELAY_DOUBLE_CLICK" />,
    /// which is what makes them one double click rather than two clicks.
    /// </summary>
    private static async System.Threading.Tasks.Task DoubleClick(Node host, Vector2 at)
    {
        Send(new InputEventMouseMotion { Position = at });
        await Frames(host, 2);

        for (int i = 0; i < 2; i++)
        {
            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = at,
                Pressed = true,
            });

            await Frames(host, 4);

            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = at,
                Pressed = false,
            });

            await Frames(host, 4);
        }

        await Frames(host, 40);
    }

    /// <summary>
    /// Run something to the end, watching the mixer while it runs, and say
    /// the first sound effect it hears.
    /// </summary>
    private static async System.Threading.Tasks.Task ListenForSound(
        Node host,
        System.Threading.Tasks.Task work
    )
    {
        string heard = null;

        while (!work.IsCompleted)
        {
            string playing = Client.Game.Audio.NowPlaying;

            if (heard == null && playing.Contains("sound:"))
            {
                heard = playing;
            }

            await Frames(host, 1);
        }

        await work;

        GD.Print(
            heard == null
                ? "[GUO] input probe: no sound effect while walking; audio is "
                  + Client.Game.Audio.NowPlaying
                : $"[GUO] input probe: audio: {heard}"
        );
    }

    /// <summary>
    /// A point on an item that the client will agree is on the item.
    /// </summary>
    /// <remarks>
    /// An item gump hit-tests against the art's own pixels, not its bounds,
    /// and a lot of item art is mostly transparent -- a dagger is a diagonal
    /// line through an empty square. Pressing the middle of the rectangle
    /// therefore misses more often than it hits, and the press lands on the
    /// container behind it instead, which is a drag of the container. Asking
    /// the control itself which pixels are solid is the only honest way to
    /// aim; the press that follows still goes through Godot like any other.
    /// </remarks>
    private static Vector2 GrabPoint(Game.UI.Controls.ItemGump item)
    {
        var centre = new Vector2(item.Width / 2f, item.Height / 2f);
        Vector2 best = centre;
        float bestDistance = float.MaxValue;

        for (int y = 0; y < item.Height; y++)
        {
            for (int x = 0; x < item.Width; x++)
            {
                if (!item.Contains(x, y))
                {
                    continue;
                }

                // Nearest solid pixel to the middle, not the first one found:
                // the first is on the edge of the art, where being a pixel out
                // is the difference between the item and the container.
                float distance = centre.DistanceSquaredTo(new Vector2(x, y));

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Vector2(x, y);
                }
            }
        }

        return new Vector2(item.ScreenCoordinateX, item.ScreenCoordinateY) + best;
    }

    /// <summary>
    /// The first item in a container gump, or the one with this serial.
    /// </summary>
    private static Game.UI.Controls.ItemGump FindItem(
        Game.UI.Controls.Control parent,
        uint serial = 0
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child is Game.UI.Controls.ItemGump item
                && (serial == 0 || item.LocalSerial == serial))
            {
                return item;
            }

            Game.UI.Controls.ItemGump found = FindItem(child, serial);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Press, move while held, release. The motion in the middle is the part
    /// that matters: the client turns a press into a drag only when the mouse
    /// moves with the button down, and it reads the position off each event.
    /// </summary>
    /// <returns>The control the press landed on, which is not always the one
    /// aimed at: items in a container overlap, and the topmost one wins.
    /// </returns>
    private static async System.Threading.Tasks.Task<Game.UI.Controls.Control> Drag(
        Node host,
        Vector2 from,
        Vector2 to
    )
    {
        Send(new InputEventMouseMotion { Position = from });
        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = from,
            Pressed = true,
        });

        await Frames(host, 6);

        Game.UI.Controls.Control pressed =
            Game.Managers.UIManager.LastControlMouseDown(Input.MouseButtonType.Left);

        GD.Print(
            $"[GUO] input probe: pressed on {pressed?.GetType().Name ?? "nothing"} "
            + $"0x{pressed?.LocalSerial ?? 0:X}"
        );

        const int steps = 8;
        Vector2 previous = from;

        for (int i = 1; i <= steps; i++)
        {
            Vector2 at = from.Lerp(to, i / (float)steps);

            Send(new InputEventMouseMotion
            {
                Position = at,
                Relative = at - previous,
                ButtonMask = MouseButtonMask.Left,
            });

            previous = at;

            await Frames(host, 4);
        }

        // Said before the release, because after it the answer is always no:
        // whether the client picked anything up is the half of a drag that a
        // failed drop hides.
        Game.UI.Controls.Control down =
            Game.Managers.UIManager.LastControlMouseDown(Input.MouseButtonType.Left);

        GD.Print(
            "[GUO] input probe: mid-drag, the cursor is holding something: "
            + Client.Game.UO.GameCursor.ItemHold.Enabled
            + $", dragging {down?.GetType().Name ?? "nothing"} 0x{down?.LocalSerial ?? 0:X}"
        );

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = to,
            Pressed = false,
        });

        await Frames(host, 40);

        return pressed;
    }

    /// <summary>
    /// Say whether a gump is up, and how big it came out.
    /// </summary>
    private static void Report<T>(string what)
        where T : Game.UI.Gumps.Gump
    {
        T gump = Game.Managers.UIManager.GetGump<T>();

        GD.Print(
            gump == null
                ? $"[GUO] input probe: no {what} gump"
                : $"[GUO] input probe: {what} gump {gump.Width}x{gump.Height} "
                  + $"with {gump.Children.Count} children"
        );
    }

    /// <remarks>
    /// A button id is not unique inside a gump. The top bar has three buttons
    /// with id 0: the Map button, and the two arrows that switch the bar
    /// between its collapsed and expanded pages -- clicking one of those by
    /// mistake folds the bar away and every later click lands on the world
    /// behind it. So the search skips the page switchers, which are the
    /// buttons with a ToPage, and anything on a page that is not showing.
    /// </remarks>
    private static Game.UI.Controls.Button FindButton(
        Game.UI.Controls.Control parent,
        int buttonId
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.Page != 0 && child.Page != parent.ActivePage)
            {
                continue;
            }

            if (child is Game.UI.Controls.Button b && b.ButtonID == buttonId && b.ToPage == 0)
            {
                return b;
            }

            Game.UI.Controls.Button found = FindButton(child, buttonId);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Say something out loud, and let the server say it back.
    /// </summary>
    /// <remarks>
    /// In the world, typing goes to the chat control without anything being
    /// clicked first, and Return sends it. The round trip is the point: the
    /// client sends a speech request, the server decides what everyone hears
    /// and sends it back, and only then does the text appear over the
    /// character and in the journal. Text on screen is therefore the server's
    /// copy, not an echo of the keypresses.
    /// </remarks>
    private static async System.Threading.Tasks.Task Speak(Node host, string what)
    {
        await Type(host, what);

        Send(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        await Frames(host, 2);
        Send(new InputEventKey { Keycode = Key.Enter, Pressed = false });

        await Frames(host, 60);

        // Take the shot here rather than leaving it to --shot-after: speech
        // over a character has a time to live of a few seconds, so a frame
        // number chosen in advance catches it only by luck.
        Client.Game.TakeScreenshot();

        await Frames(host, 60);

        // The journal is where the server's copy lands, so it is the evidence
        // that the round trip happened rather than that the text was typed.
        var entries = Game.Managers.JournalManager.Entries;
        int from = System.Math.Max(0, entries.Count - 4);

        for (int i = from; i < entries.Count; i++)
        {
            GD.Print($"[GUO] journal: {entries[i].Name}: {entries[i].Text}");
        }
    }

    /// <summary>
    /// Hold the right button away from the character, which is how UO walks.
    /// </summary>
    /// <remarks>
    /// This is the first thing that asks the shard for something and waits to
    /// be told whether it happened: the client sends a move request, the
    /// server accepts or rejects it, and the player only moves on the reply.
    /// A screenshot cannot show that on its own, so the position is printed
    /// either side of it.
    /// </remarks>
    private static readonly Vector2[] Offsets =
    {
        new(1, 1),
        new(-1, 1),
        new(-1, -1),
        new(1, -1),
    };

    private static async System.Threading.Tasks.Task Walk(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World?.Player;

        if (player == null)
        {
            GD.Print("[GUO] input probe: no player; not in the world");

            return;
        }

        GD.Print($"[GUO] input probe: player at {player.X},{player.Y}");

        // Each of the four screen diagonals in turn. One direction can be a
        // wall -- the starting spot is indoors -- and four cannot all be.
        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;
        var centre = new Vector2(bounds.Width / 2f, bounds.Height / 2f);

        foreach (Vector2 offset in Offsets)
        {
            Vector2 at = centre + offset * 250f;

            Send(new InputEventMouseMotion { Position = at });

            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Position = at,
                Pressed = true,
            });

            // Held. The scene walks a step at a time for as long as the
            // button is down, so this is how far the character goes.
            await Frames(host, 90);

            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Position = at,
                Pressed = false,
            });

            await Frames(host, 20);

            GD.Print(
                $"[GUO] input probe: pulled {offset.X},{offset.Y} -> player at "
                + $"{player.X},{player.Y}, facing {player.Direction}, steps "
                + $"{player.Walker.StepsCount}, failed {player.Walker.WalkingFailed}"
            );
        }
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

    /// <summary>
    /// Time a stretch of frames in the world, with everything the probe has
    /// opened still on screen.
    /// </summary>
    /// <remarks>
    /// The average says little on its own -- the window is vsynced, so a
    /// client with room to spare and a client with none both report about
    /// sixteen milliseconds. The worst frame is the number that matters: a
    /// hitch is what a player feels, and it is the first thing a change to
    /// the batcher or the world mesh breaks.
    /// </remarks>
    private static async System.Threading.Tasks.Task MeasureFrames(Node host, int count)
    {
        ulong start = Godot.Time.GetTicksUsec();
        ulong previous = start;
        ulong worst = 0;

        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

            ulong now = Godot.Time.GetTicksUsec();
            ulong frame = now - previous;
            previous = now;

            if (frame > worst)
            {
                worst = frame;
            }
        }

        double total = (previous - start) / 1000.0;

        GD.Print(
            $"[GUO] input probe: {count} frames in {total:F0} ms "
            + $"({total / count:F2} ms average, {worst / 1000.0:F2} ms worst, "
            + $"{count * 1000.0 / total:F1} fps)"
        );
    }

    private static async System.Threading.Tasks.Task Frames(Node host, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
