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

    /// <summary>
    /// Every expectation the run has checked, in order.
    /// </summary>
    private static readonly System.Collections.Generic.List<(string What, bool Ok)> Checks =
        new();

    /// <summary>
    /// True when every check passed. Read by the host to decide the exit
    /// code, which is what turns this from a thing somebody reads into a
    /// thing that can fail a build.
    /// </summary>
    public static bool Passed { get; private set; }

    /// <summary>
    /// Record an expectation and say whether it held.
    /// </summary>
    private static void Check(string what, bool ok, string detail = null)
    {
        Checks.Add((what, ok));

        GD.Print(
            $"[GUO] probe check: {(ok ? "ok  " : "FAIL")} {what}"
            + (detail == null ? "" : $" -- {detail}")
        );
    }

    public static async System.Threading.Tasks.Task Run(Node host, int settleFrames)
    {
        await EnterTheWorld(host, settleFrames);

        Check(
            "the character is in the world",
            Client.Game.UO.World.InGame,
            Client.Game.UO.World.Player == null
                ? "no player"
                : $"{Client.Game.UO.World.Player.Name} at "
                  + $"{Client.Game.UO.World.Player.X},{Client.Game.UO.World.Player.Y}"
        );

        GD.Print("[GUO] input probe: full-size game window");

        FullSizeGameWindow();

        await Frames(host, 120);

        GD.Print("[GUO] input probe: walking");

        // A step plays a footstep, so this is where a sound effect can be
        // listened for. It has to be caught while it is playing: an effect is
        // about a second long, and by the end of the run there is nothing
        // left to hear but the music, which says nothing about UOSound.
        Check("the character walks", await ListenForSound(host, Walk(host)));

        await RunTheRest(host);
    }

    /// <summary>
    /// Log the probe's account in and get its character into the world.
    /// </summary>
    /// <remarks>
    /// Separate from the run because it is not only the probe that needs it:
    /// a dev shard is administered from inside the game, so the tool that
    /// generates the world has to get into the world first.
    /// </remarks>
    public static async System.Threading.Tasks.Task EnterTheWorld(Node host, int settleFrames)
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
    }

    /// <summary>Everything the run does once the character is walking.</summary>
    private static async System.Threading.Tasks.Task RunTheRest(Node host)
    {
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

        // While the skills list is still the newest thing on screen: the
        // status gump overlaps it, and a covered button is not clickable.
        GD.Print("[GUO] input probe: using a skill");

        await UseFirstSkill(host);

        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 8, "Status");

        GD.Print("[GUO] input probe: clicking someone else");

        await ClickSomeoneElse(host);

        GD.Print("[GUO] input probe: the journal, the options and war mode");

        // The paperdoll's own buttons first. Anything opened before them may
        // land on top of the paperdoll, and then the click goes to whatever
        // is covering it -- which is how the journal, opened first, quietly
        // ate both of these.
        //
        // PaperDollGump.Buttons: Options is 1 and PeaceWarToggle is 7.
        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 1, "Options");
        Report<Game.UI.Gumps.OptionsGump>("options");

        // Closed again rather than left up: it is the largest gump in the
        // client and it would cover everything the probe clicks after this.
        Game.Managers.UIManager.GetGump<Game.UI.Gumps.OptionsGump>()?.Dispose();

        await Frames(host, 30);

        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 7, "war mode");

        Check("war mode goes on", Client.Game.UO.World.Player.InWarMode);

        await ClickGumpButton<Game.UI.Gumps.PaperDollGump>(host, 7, "war mode");

        // The journal comes off the top bar (Buttons.Journal is 3 there too):
        // the paperdoll only grows a journal button on clients older than
        // 5.0.0a, and this one is 7.0.107.
        await ClickGumpButton<Game.UI.Gumps.TopBarGump>(host, 3, "Journal");
        Report<Game.UI.Gumps.JournalGump>("journal");

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
                open.Add($"{g.GetType().Name}@{g.X},{g.Y} {g.Width}x{g.Height}");
            }
        }

        GD.Print($"[GUO] input probe: gumps open: {string.Join(", ", open)}");
        GD.Print($"[GUO] input probe: audio: {Client.Game.Audio.NowPlaying}");

        int passed = 0;

        foreach ((string what, bool ok) in Checks)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                GD.PrintErr($"[GUO] probe FAILED: {what}");
            }
        }

        Passed = passed == Checks.Count;

        GD.Print($"[GUO] input probe: {passed}/{Checks.Count} checks passed");
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

        Report<Game.UI.Gumps.ContainerGump>("backpack");
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

        Check(
            "the item is in the backpack after the drop",
            moved != null,
            moved == null
                ? $"0x{serial:X} is gone"
                : $"0x{serial:X} at {moved.ScreenCoordinateX},{moved.ScreenCoordinateY}"
        );

        Check(
            "the cursor is empty after the drop",
            !Client.Game.UO.GameCursor.ItemHold.Enabled
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

        Vector2? found = await FindCharacter(host);

        Check(
            "the character can be picked out of the world",
            found != null,
            found == null ? "nothing found in the column" : $"at {found.Value.X},{found.Value.Y}"
        );

        if (found == null)
        {
            return;
        }

        await DoubleClick(host, found.Value);

        await Frames(host, 90);

        Report<Game.UI.Gumps.PaperDollGump>("paperdoll");
    }

    /// <summary>
    /// Where on screen the character is, asked rather than worked out.
    /// </summary>
    /// <remarks>
    /// The camera keeps the player in the middle of the window, but the middle
    /// of the window is a floor tile: a mobile is drawn standing on its tile,
    /// so its body is above that point, and how far above depends on the body.
    /// The probe moves the mouse up the column and asks the client what is
    /// under it -- the same hit test a player's own aiming relies on.
    /// </remarks>
    private static async System.Threading.Tasks.Task<Vector2?> FindCharacter(Node host)
    {
        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;
        var centre = new Vector2(bounds.Width / 2f, bounds.Height / 2f);

        for (int dy = 0; dy >= -120; dy -= 10)
        {
            var at = new Vector2(centre.X, centre.Y + dy);

            Send(new InputEventMouseMotion { Position = at });

            await Frames(host, 4);

            if (ReferenceEquals(Game.SelectedObject.Object, Client.Game.UO.World.Player))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>
    /// Press the use button on the first skill that has one, and target the
    /// character with it.
    /// </summary>
    /// <remarks>
    /// The only thing here that goes through the target cursor, which is how
    /// half of what a player does in UO is done -- casting, healing, tracking,
    /// anything that asks "on what?". The skill is whichever one the gump
    /// lists first with a use button, because which skills are usable depends
    /// on the character and the shard, and the path being checked is the same
    /// for all of them.
    /// </remarks>
    private static async System.Threading.Tasks.Task UseFirstSkill(Node host)
    {
        Game.UI.Gumps.StandardSkillsGump skills =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.StandardSkillsGump>();

        if (skills == null)
        {
            Check("a skill can be used", false, "the skills gump is not open");

            return;
        }

        // Out from under the paperdoll it was opened from. Gumps open on top
        // of each other at the client's default positions, and a click goes to
        // whichever gump is in front -- the skills list was underneath, so
        // every press on it landed on the paperdoll's backdrop instead. A
        // player drags the window clear; the probe puts it clear.
        skills.X = 900;
        skills.Y = 400;

        await Frames(host, 10);

        // Every group starts collapsed, so the skills themselves are laid out
        // but not drawn. 1000 is a group's own expand button, and the groups
        // have to be opened one at a time because opening one moves the ones
        // below it.
        for (int i = 0; i < 16; i++)
        {
            var collapsed = new System.Collections.Generic.List<Game.UI.Controls.Button>();

            Collect(skills, 1000, collapsed);

            Game.UI.Controls.Button next = collapsed.Count > i ? collapsed[i] : null;

            if (next == null)
            {
                break;
            }

            await Click(host, Centre(next));
            await Frames(host, 10);
        }

        var buttons = new System.Collections.Generic.List<Game.UI.Controls.Button>();

        Collect(skills, 0, buttons);

        // Id 0 is also the gump's own "new group" button, which is not a skill
        // and which leaves a group behind on the character when pressed. A
        // skill's row is the one that carries the skill's name.
        var uses = new System.Collections.Generic.List<Game.UI.Controls.Button>();

        foreach (Game.UI.Controls.Button candidate in buttons)
        {
            if (SkillNameOf(candidate) != "?")
            {
                uses.Add(candidate);
            }
        }

        GD.Print($"[GUO] input probe: {uses.Count} skills can be used");

        // Anatomy by preference: it is the plainest targeted skill in the
        // list, every character has it, and using it on someone always asks
        // who. Any other usable skill would still exercise the gump; not all
        // of them raise a target cursor.
        Game.UI.Controls.Button use = null;

        foreach (Game.UI.Controls.Button candidate in uses)
        {
            if (SkillNameOf(candidate) == "Anatomy")
            {
                use = candidate;

                break;
            }
        }

        if (use == null && uses.Count > 0)
        {
            use = uses[0];
        }

        if (use == null)
        {
            Check(
                "a skill can be used",
                false,
                $"none of the {buttons.Count} buttons found is a skill's"
            );

            return;
        }

        GD.Print($"[GUO] input probe: using {SkillNameOf(use)}");

        await Click(host, Centre(use));

        await Frames(host, 60);

        bool targeting = Client.Game.UO.World.TargetManager.IsTargeting;

        Check("a skill asks for a target", targeting);

        if (!targeting)
        {
            return;
        }

        Vector2? character = await FindCharacter(host);

        if (character == null)
        {
            Check("the target lands on the character", false, "the character is not on screen");

            return;
        }

        await Click(host, character.Value);

        await Frames(host, 90);

        Check(
            "the target is taken",
            !Client.Game.UO.World.TargetManager.IsTargeting,
            LastJournalLine()
        );
    }

    /// <summary>
    /// Click the nearest person who is not the player, and let the server say
    /// who they are.
    /// </summary>
    /// <remarks>
    /// Everything the probe has clicked in the world so far has been the
    /// player's own body. This is the other half: a mobile the shard sent,
    /// drawn from its own position, hit-tested where the client thinks it is,
    /// and named by the server in reply to the click.
    /// </remarks>
    private static async System.Threading.Tasks.Task ClickSomeoneElse(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        Vector2? self = await FindCharacter(host);

        if (self == null)
        {
            Check("someone else is on screen", false, "the player is not on screen");

            return;
        }

        // Nearest first: the further away a mobile is, the more likely
        // something is drawn in front of it.
        var others = new System.Collections.Generic.List<Game.GameObjects.Mobile>();

        foreach (Game.GameObjects.Mobile mobile in Client.Game.UO.World.Mobiles.Values)
        {
            if (mobile.Serial != player.Serial)
            {
                others.Add(mobile);
            }
        }

        others.Sort(
            (a, b) =>
                (System.Math.Abs(a.X - player.X) + System.Math.Abs(a.Y - player.Y)).CompareTo(
                    System.Math.Abs(b.X - player.X) + System.Math.Abs(b.Y - player.Y)
                )
        );

        GD.Print($"[GUO] input probe: {others.Count} other mobiles are known");

        bool seen = false;
        int tried = 0;

        foreach (Game.GameObjects.Mobile mobile in others)
        {
            if (tried >= 4)
            {
                break;
            }

            // The player is drawn at `self`, so everyone else is drawn at
            // `self` plus the difference between the two isometric positions.
            // Recomputed right before the click, and confirmed by hovering:
            // townspeople walk, and a position worked out a second ago puts
            // the click on the floor they were standing on -- which the server
            // answers, politely, with "marble floor".
            Vector2 At() =>
                new(
                    self.Value.X + (mobile.RealScreenPosition.X - player.RealScreenPosition.X),
                    self.Value.Y + (mobile.RealScreenPosition.Y - player.RealScreenPosition.Y)
                );

            Send(new InputEventMouseMotion { Position = At() });

            await Frames(host, 4);

            if (!ReferenceEquals(Game.SelectedObject.Object, mobile))
            {
                continue;
            }

            if (!seen)
            {
                Check(
                    "someone else is on screen",
                    true,
                    $"{(string.IsNullOrEmpty(mobile.Name) ? "unnamed" : mobile.Name)} 0x{mobile.Serial:X}"
                );

                seen = true;
            }

            tried++;

            await Click(host, At());

            await Frames(host, 40);

            // Not "did this one answer": the client asks for a name only when
            // it does not already have one, so a click on somebody it has
            // heard of is answered by silence. What is worth checking is that
            // the names came from the server at all -- every line here is a
            // mobile the client drew, asked about, and was told the name of.
            int named = 0;

            var entries = Game.Managers.JournalManager.Entries;

            for (int e = 0; e < entries.Count; e++)
            {
                foreach (Game.GameObjects.Mobile known in others)
                {
                    if (entries[e].Name == known.Name)
                    {
                        named++;

                        break;
                    }
                }
            }

            Check(
                "the shard's people name themselves",
                named > 0,
                $"{named} of {entries.Count} journal lines are townspeople"
            );

            return;
        }

        if (seen)
        {
            Check("the shard's people name themselves", false, "nobody could be clicked");

            return;
        }

        Check(
            "someone else is on screen",
            false,
            $"none of the {others.Count} mobiles the shard sent could be hit"
        );
    }

    /// <summary>Every visible button with this id, in the order they are laid out.</summary>
    private static void Collect(
        Game.UI.Controls.Control parent,
        int buttonId,
        System.Collections.Generic.List<Game.UI.Controls.Button> into
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (!child.IsVisible || (child.Page != 0 && child.Page != parent.ActivePage))
            {
                continue;
            }

            if (child is Game.UI.Controls.Button b && b.ButtonID == buttonId && b.ToPage == 0)
            {
                into.Add(b);
            }

            Collect(child, buttonId, into);
        }
    }

    /// <summary>
    /// The skill a use button belongs to, read off the label beside it.
    /// </summary>
    /// <remarks>
    /// The row is a private class inside the gump, so the name is not
    /// reachable from here -- but the row draws it, and that label is the
    /// button's own sibling.
    /// </remarks>
    private static string SkillNameOf(Game.UI.Controls.Button use)
    {
        if (use.Parent == null)
        {
            return "?";
        }

        foreach (Game.UI.Controls.Control sibling in use.Parent.Children)
        {
            // The row carries two labels, the name and the current value, and
            // which comes first is the gump's business -- the name is the one
            // that is not a number.
            if (sibling is Game.UI.Controls.Label label
                && !string.IsNullOrEmpty(label.Text)
                && !double.TryParse(label.Text, out _))
            {
                return label.Text;
            }
        }

        return "?";
    }

    /// <summary>
    /// Type a line into the game window and press enter, which is how the
    /// client says anything to the server -- speech, and on a dev shard the
    /// administration commands too.
    /// </summary>
    public static async System.Threading.Tasks.Task Say(Node host, string what)
    {
        await Type(host, what);

        Send(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        await Frames(host, 2);
        Send(new InputEventKey { Keycode = Key.Enter, Pressed = false });
    }

    /// <summary>Let the client run for a number of drawn frames.</summary>
    public static async System.Threading.Tasks.Task Wait(Node host, int frames) =>
        await Frames(host, frames);

    /// <summary>The middle of a control, in screen pixels.</summary>
    private static Vector2 Centre(Game.UI.Controls.Control control) =>
        new(
            control.ScreenCoordinateX + control.Width / 2f,
            control.ScreenCoordinateY + control.Height / 2f
        );

    /// <summary>The most recent line in the journal, for a check's detail.</summary>
    private static string LastJournalLine()
    {
        var entries = Game.Managers.JournalManager.Entries;

        return entries.Count == 0
            ? "the journal is empty"
            : $"{entries[entries.Count - 1].Name}: {entries[entries.Count - 1].Text}";
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
    private static async System.Threading.Tasks.Task<T> ListenForSound<T>(
        Node host,
        System.Threading.Tasks.Task<T> work
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

        Check(
            "a sound effect plays",
            heard != null,
            heard ?? $"only {Client.Game.Audio.NowPlaying}"
        );

        return await work;
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

        Check(
            "the drag picks something up",
            Client.Game.UO.GameCursor.ItemHold.Enabled,
            $"{down?.GetType().Name ?? "nothing"} 0x{down?.LocalSerial ?? 0:X}"
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

        Check(
            $"the {what} opens",
            gump != null,
            gump == null
                ? "not there"
                : $"{gump.Width}x{gump.Height} with {gump.Children.Count} children"
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
            // A control that is not drawn cannot be clicked, and the skills
            // gump keeps a whole collapsed group's buttons laid out but
            // invisible, so this is the difference between a button and a
            // button-shaped hole.
            if (!child.IsVisible)
            {
                continue;
            }

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
        await Say(host, what);

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
        bool echoed = false;

        for (int i = from; i < entries.Count; i++)
        {
            GD.Print($"[GUO] journal: {entries[i].Name}: {entries[i].Text}");

            echoed |= entries[i].Text == what;
        }

        Check("the server sends the speech back", echoed, $"\"{what}\"");
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

    private static async System.Threading.Tasks.Task<bool> Walk(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World?.Player;

        if (player == null)
        {
            GD.Print("[GUO] input probe: no player; not in the world");

            return false;
        }

        GD.Print($"[GUO] input probe: player at {player.X},{player.Y}");

        // Where it started, so that "it walked" is a comparison and not an
        // impression. Checked after every pull rather than at the end: the
        // four directions are opposite pairs, so a character that walks all
        // four of them finishes where it began.
        int startX = player.X;
        int startY = player.Y;
        bool moved = false;

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

            moved |= player.X != startX || player.Y != startY;
        }

        return moved;
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

        double fps = count * 1000.0 / total;

        // Fifty, not sixty: the window is vsynced to sixty and the machine
        // running this is not always idle. What this is here to catch is a
        // change that halves the frame rate, not the odd busy second.
        Check(
            "the world holds its frame rate",
            fps >= 50,
            $"{count} frames in {total:F0} ms ({total / count:F2} ms average, "
            + $"{worst / 1000.0:F2} ms worst, {fps:F1} fps)"
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
