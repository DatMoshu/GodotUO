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
    /// The account the probe logs in with: the dev shard's owner
    /// (UO_SHARD_OWNER), whose password the shard's first run generates.
    /// </summary>
    private const string DefaultAccount = "guoprobe";

    private const string DefaultCharacter = "Guoprobe";

    /// <summary>
    /// The account this process logs in with. --account overrides it so that
    /// several clients can share one shard without sharing a session.
    /// </summary>
    public static string ProbeAccount { get; set; } = DefaultAccount;

    public static string ProbePassword { get; set; } = DevShardPassword(DefaultAccount);

    /// <summary>
    /// The dev shard's password for an account when no --password is given.
    /// The owner and the game master accounts have generated passwords
    /// (tools/modernuo/configure.py writes them to the per-user workspace;
    /// the launchers export them as UO_SHARD_OWNER_PASSWORD and
    /// UO_SHARD_GM_PASSWORD). Any other account is a player that auto account
    /// creation makes with whatever it is given, so its name will do.
    /// </summary>
    public static string DevShardPassword(string account)
    {
        string owner = System.Environment.GetEnvironmentVariable("UO_SHARD_OWNER");
        string password = null;

        if (string.Equals(account, string.IsNullOrEmpty(owner) ? DefaultAccount : owner, System.StringComparison.OrdinalIgnoreCase))
        {
            password = System.Environment.GetEnvironmentVariable("UO_SHARD_OWNER_PASSWORD");
        }
        else
        {
            string gms = System.Environment.GetEnvironmentVariable("UO_SHARD_GM_ACCOUNTS") ?? "";

            foreach (string gm in gms.Split(',', ';', ' '))
            {
                if (string.Equals(gm.Trim(), account, System.StringComparison.OrdinalIgnoreCase))
                {
                    password = System.Environment.GetEnvironmentVariable("UO_SHARD_GM_PASSWORD");
                    break;
                }
            }
        }

        return string.IsNullOrEmpty(password) ? account : password;
    }

    /// <summary>
    /// Upstream's own -username/-password/-autologin path drives the login,
    /// so EnterTheWorld must not type or click: its keystrokes would land in
    /// whatever screen autologin is on. --autologin sets it (layer_shot.bat).
    /// </summary>
    public static bool AutoLogin { get; set; }

    /// <summary>
    /// The character to play, by name, or null for whichever the character
    /// list has selected -- the last one played, which is what a person's
    /// own playtest expects. --character sets it: the shard does not refuse
    /// an account that is already online, so four clients on the owner
    /// account, each on its own character, all have the owner's commands.
    /// A named character that does not exist is made.
    /// </summary>
    public static string ProbeCharacter { get; set; }

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
    public static async System.Threading.Tasks.Task EnterTheWorld(
        Node host,
        int settleFrames,
        string account = null,
        string password = null,
        string character = null
    )
    {
        account ??= ProbeAccount;
        password ??= ProbePassword;
        character ??= ProbeCharacter;
        bool byName = character != null;
        character ??= DefaultCharacter;

        await Frames(host, settleFrames);

        if (!AutoLogin)
        {
            GD.Print("[GUO] input probe: typing the account");

            await Click(host, AccountField);
            await Type(host, account);

            await Click(host, PasswordField);
            await Type(host, password);

            GD.Print("[GUO] input probe: clicking Login");

            await Click(host, LoginButton);

            // The handshake, then the shard list.
            await Frames(host, 120);

            GD.Print("[GUO] input probe: selecting the shard");

            await Click(host, ShardEntry);

            await Frames(host, 180);
        }
        else
        {
            GD.Print("[GUO] input probe: autologin drives; waiting for the world");
        }

        // What comes next depends on the account, not on the probe. The first
        // run makes the character and the client goes straight into creation;
        // every run after that stops at the character list with that same
        // character on it. Reading which gump is up is the only way to know:
        // walking the creation pages blind on a returning account pressed the
        // list's forward arrow with nothing chosen, and the probe sat at a
        // loading screen for the rest of the run. The list can lag behind a
        // slow shard (world saves stall it), so poll for it instead of
        // deciding on one frame; --autologin may already have walked past it.
        bool creating = true;
        bool sawList = false;
        for (int i = 0; i < 150; i++)
        {
            if (Client.Game.UO.World.InGame)
            {
                await Frames(host, 240);
                return;
            }

            if (Game.Managers.UIManager.GetGump<Game.UI.Gumps.Login.CharacterSelectionGump>() != null)
            {
                creating = false;
                sawList = true;
                break;
            }

            await Frames(host, 2);
        }

        if (creating && !sawList && !Client.Game.UO.World.InGame)
        {
            // Neither the world nor the character list appeared (dead shard,
            // rejected login): typing a name now would land nowhere, exactly
            // the silent failure this guard replaces.
            Check("the character list appeared", false, OpenLoginGumps());
            return;
        }

        if (!creating)
        {
            Game.Scenes.LoginScene loginScene = Client.Game.GetScene<Game.Scenes.LoginScene>();
            int slot = System.Array.IndexOf(loginScene?.Characters ?? System.Array.Empty<string>(), character);

            if (!byName)
            {
                GD.Print("[GUO] input probe: the account has a character; logging it in");

                // CharacterSelectionGump.Buttons: Next is 2, and it logs in
                // whichever character is selected, which is the last played.
                await ClickGumpButton<Game.UI.Gumps.Login.CharacterSelectionGump>(host, 2, "Next");
            }
            else if (slot >= 0)
            {
                GD.Print($"[GUO] input probe: the account has {character}; logging it in");

                // Which character is picked is set-up, not what is under test,
                // so the scene is told directly rather than the list clicked:
                // the entries lay themselves out and the list remembers the
                // last character per account, which another client on the
                // same account may have changed a moment ago.
                loginScene.SelectCharacter((uint) slot);
            }
            else
            {
                GD.Print($"[GUO] input probe: the account has no {character}; making one");

                // CharacterSelectionGump.Buttons: New is 0.
                await ClickGumpButton<Game.UI.Gumps.Login.CharacterSelectionGump>(host, 0, "New");

                await Frames(host, 120);

                if (FindControl<Game.UI.Gumps.CharCreation.CreateCharAppearanceGump>() == null)
                {
                    // A full account (or a missed New click) leaves no
                    // creation UI: fail loud instead of naming the void.
                    Check("character creation opened", false, OpenLoginGumps());
                    return;
                }

                creating = true;
            }
        }

        if (creating)
        {
            GD.Print("[GUO] input probe: naming the character");

            await Click(host, CharacterName);
            await Type(host, character);

            // The creation steps are child gumps of CharCreationGump, and the
            // fixed 640x480 points above them missed this client's layout
            // (the Next arrow landed on background art). Click the controls
            // themselves: CreateCharAppearanceGump.Buttons.Next is 6.
            await ClickChildButton<Game.UI.Gumps.CharCreation.CreateCharAppearanceGump>(host, 6, "Next", true);

            GD.Print("[GUO] input probe: choosing a profession");

            await ClickPlainProfession(host);

            GD.Print("[GUO] input probe: accepting the starting city");

            // CreateCharSelectionCityGump: a city's button is 2 + its index; Finish is 1.
            await ClickChildButton<Game.UI.Gumps.CharCreation.CreateCharSelectionCityGump>(host, 2, "first city", false);
            await ClickChildButton<Game.UI.Gumps.CharCreation.CreateCharSelectionCityGump>(host, 1, "Finish", true);
        }

        await Frames(host, 240);

        // On a device the typed account can land in the world's chat entry (the
        // Thor's showed "Guoprobe" after login), and the first thing said then
        // carries it. A player starts with an empty line; so does the probe.
        Game.UI.Gumps.SystemChatControl chat = Game.Managers.UIManager.SystemChat;

        if (Client.Game.UO.World.InGame && chat != null && !chat.IsDisposed && chat.TextBoxControl.Text.Length > 0)
        {
            GD.Print($"[GUO] input probe: cleared \"{chat.TextBoxControl.Text}\" left in the chat entry by the login");
            chat.TextBoxControl.ClearText();
        }
    }

    /// <summary>
    /// Seconds to keep playing at the end of the run, watching for drift.
    /// Zero -- the default -- skips it, so an ordinary playtest stays short.
    /// </summary>
    public static int EndureSeconds { get; set; }

    /// <summary>
    /// A map target for walk-to runs ("x,y[,z]"): log in, pathfind there with
    /// the client's own pathfinder, and stop. Null for the ordinary session.
    /// </summary>
    public static string WalkToTarget { get; set; }

    /// <summary>Staged sound/music ids a walk-to run plays on arrival (live audio proof).</summary>
    public static int? WalkToSound { get; set; }

    /// <summary>Staged music id a walk-to run plays on arrival (live audio proof).</summary>
    public static int? WalkToMusic { get; set; }
    public static string WalkToMusicExpect { get; set; } = "";

    /// <summary>Staged splat a walk-to run clicks on arrival (live picking proof).</summary>
    public static string WalkToClickSplat { get; set; }

    /// <summary>Voice profile a walk-to run speaks with (live TTS proof).</summary>
    public static string WalkToVoice { get; set; }

    /// <summary>Line a walk-to run speaks on arrival (live TTS proof).</summary>
    public static string WalkToSay { get; set; }

    /// <summary>Seconds a walk-to run listens for voiced mobiles (live mob-voice proof).</summary>
    public static int WalkToHearSecs { get; set; }

    /// <summary>Line delivered as the nearest mapped mobile (mob path proof).</summary>
    public static string WalkToVoiceFake { get; set; }

    /// <summary>Design a voice for the nearest unmapped mobile first (design proof).</summary>
    public static bool WalkToDesignNearby { get; set; }

    /// <summary>Theme folder, name and zone a walk-to run applies on arrival (live theme proof).</summary>
    public static string WalkToThemeDir { get; set; }

    /// <summary>Theme folder, name and zone a walk-to run applies on arrival (live theme proof).</summary>
    public static string WalkToTheme { get; set; }

    /// <summary>Theme folder, name and zone a walk-to run applies on arrival (live theme proof).</summary>
    public static string WalkToThemeZone { get; set; }

    /// <summary>
    /// Log in, walk to <see cref="WalkToTarget"/> and stop, for a screenshot
    /// of a particular place (a staged splat's site, say). The walk is the
    /// client's own pathfinder, unsteered; arrival is within 2 tiles.
    /// </summary>
    public static async System.Threading.Tasks.Task RunWalkTo(Node host)
    {
        await EnterTheWorld(host, 200);

        // The profile restores its saved window position at login, which wins
        // over the startup pin and has left scripted windows where no screen
        // covers them. Re-assert the pin now that the world is up.
        if (GameController.PinnedWindowPosition.HasValue)
        {
            Godot.DisplayServer.WindowSetPosition(GameController.PinnedWindowPosition.Value);
        }

        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;
        Check(
            "the character is in the world",
            Client.Game.UO.World.InGame && player != null,
            player == null ? "no player" : $"{player.Name} at {player.X},{player.Y}"
        );

        if (!Client.Game.UO.World.InGame || player == null)
        {
            return;
        }

        string[] parts = (WalkToTarget ?? "").Split(',');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out int gx)
            || !int.TryParse(parts[1], out int gy))
        {
            Check("walk-to target parses", false, WalkToTarget ?? "");
            return;
        }

        int gz = parts.Length > 2 && int.TryParse(parts[2], out int z) ? z : player.Z;
        GD.Print($"[GUO] input probe: walking to {gx},{gy},{gz} from {player.X},{player.Y},{player.Z}");

        bool started = player.Pathfinder.WalkTo(gx, gy, gz, 1);
        if (!started)
        {
            // No walkable path (an island spawn, say): an admin teleport
            // through the shard's own tools moves the character instead, and
            // the walk resumes from wherever it lands. Ten minutes, then give
            // up honestly rather than walking nowhere.
            GD.Print($"[GUO] input probe: no path from {player.X},{player.Y}; waiting for an admin teleport");
            int sx = player.X, sy = player.Y;
            bool moved = false;
            for (int wait = 0; wait < 120 && !moved; wait++)
            {
                await Frames(host, 50);
                moved = player.X != sx || player.Y != sy;
            }

            Check("an admin teleport moved the character", moved, $"still at {player.X},{player.Y}");
            if (!moved)
            {
                return;
            }

            GD.Print($"[GUO] input probe: teleported to {player.X},{player.Y},{player.Z}; retrying the walk");
            started = player.Pathfinder.WalkTo(gx, gy, gz, 1);
        }

        Check("the pathfinder found a path", started, $"from {player.X},{player.Y}");
        if (!started)
        {
            return;
        }

        // Autowalk is stepped by the game scene, so this only waits: up to
        // eight minutes for a cross-map trek, checking every ~10 frames.
        for (int wait = 0; wait < 48 * 60 && player.Pathfinder.AutoWalking; wait++)
        {
            await Frames(host, 10);
        }

        player.Pathfinder.StopAutoWalk();
        int dist = System.Math.Max(System.Math.Abs(gx - player.X), System.Math.Abs(gy - player.Y));
        GD.Print($"[GUO] input probe: walk-to ended at {player.X},{player.Y},{player.Z} ({dist} tiles off)");

        // Live audio proof: the staged clips through the real audio path.
        // A sound effect is seconds long, so it is checked while starting;
        // music is read back from what is playing afterwards.
        if (WalkToSound is int sfx)
        {
            Client.Game.Audio.PlaySound(sfx);
            await Frames(host, 30);
            string playing = Client.Game.Audio.NowPlaying;
            Check($"staged sound {sfx} plays", playing.Contains($"sound:pack-sound-{sfx}"), playing);
        }

        if (WalkToMusic is int mus)
        {
            Client.Game.Audio.PlayMusic(mus);
            await Frames(host, 60);
            string playing = Client.Game.Audio.NowPlaying;
            Check($"staged music {mus} plays", playing.Contains("music:"), playing);
        }

        // Live music-zone proof: standing in a zone starts its round-robin;
        // waiting for the second track proves advance-on-end (cycling).
        if (!string.IsNullOrWhiteSpace(WalkToMusicExpect))
        {
            string status = "";
            for (int i = 0; i < 120 && !status.Contains(WalkToMusicExpect); i++)
            {
                await Frames(host, 30);
                status = GUO.IO.Audio.MusicZones.Status ?? "";
            }

            Check($"music zone plays '{WalkToMusicExpect}'", status.Contains(WalkToMusicExpect), status);
        }

        // Live theme proof: activate the theme over its zone and repaint.
        // Themed statics keep the world's own pixels (chunk meshes refresh),
        // so the screenshot afterwards is the evidence.
        if (!string.IsNullOrWhiteSpace(WalkToTheme))
        {
            string path = System.IO.Path.Combine(WalkToThemeDir ?? "", WalkToTheme + ".theme.json");
            if (!System.IO.File.Exists(path))
            {
                path = System.IO.Path.Combine(WalkToThemeDir ?? "", WalkToTheme + ".json");
            }

            Check($"theme {WalkToTheme} loads", System.IO.File.Exists(path), path);
            if (System.IO.File.Exists(path))
            {
                var theme = Game.Managers.Theme.Load(path);
                var zones = new System.Collections.Generic.List<Game.Managers.ThemeZone>();
                if (!string.IsNullOrWhiteSpace(WalkToThemeZone))
                {
                    string[] zoneParts = WalkToThemeZone.Split(',');
                    if (zoneParts.Length >= 5
                        && int.TryParse(zoneParts[0], out int f) && int.TryParse(zoneParts[1], out int x1)
                        && int.TryParse(zoneParts[2], out int y1) && int.TryParse(zoneParts[3], out int x2)
                        && int.TryParse(zoneParts[4], out int y2))
                    {
                        zones.Add(new Game.Managers.ThemeZone { Facet = f, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
                    }
                }

                Check($"theme {WalkToTheme} has a zone", zones.Count > 0, WalkToThemeZone ?? "");
                Game.Managers.ThemeManager.Activate(theme, zones);
                Game.Managers.ThemeManager.Reapply(Client.Game.UO.World);
                await Frames(host, 60);
                int repainted = 0;
                foreach (var chunk in Client.Game.UO.World.Map.GetUsedChunks())
                {
                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            for (Game.GameObjects.GameObject o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                            {
                                if (o is Game.GameObjects.Static && o.Graphic >= 0xF000)
                                {
                                    repainted++;
                                }
                            }
                        }
                    }
                }

                Check($"theme {WalkToTheme} repainted statics", repainted > 0, $"{repainted} themed");
            }
        }

        // Live TTS proof: speak the line in the voice profile. Synthesis
        // takes minutes (ComfyUI queue), so poll the driver status; the game
        // stays responsive while it works.
        if (!string.IsNullOrWhiteSpace(WalkToVoice) && !string.IsNullOrWhiteSpace(WalkToSay))
        {
            GUO.IO.Audio.VoiceManager.ProfileName = WalkToVoice;
            GD.Print($"[GUO] voice: profiles in {GUO.IO.Audio.VoiceManager.ProfilesDir()}");
            Game.GameActions.Say(WalkToSay);
            bool spoke = false;
            for (int i = 0; i < 120 && !spoke; i++)
            {
                await Frames(host, 30);
                string st = GUO.IO.Audio.VoiceManager.Status;
                spoke = st.StartsWith("playing") || st.StartsWith("ready") || st.StartsWith("done");
                if (st.StartsWith("error"))
                {
                    break;
                }
            }

            Check($"voice speaks '{WalkToSay}'", spoke, GUO.IO.Audio.VoiceManager.Status);
        }

        // Design proof: the nearest mobile without a mapped voice gets an
        // LLM-designed voice (description + sample + map rule), then speaks.
        if (WalkToDesignNearby && !string.IsNullOrWhiteSpace(WalkToVoiceFake))
        {
            Game.GameObjects.Mobile candidate = null;
            int candidateDist = int.MaxValue;
            foreach (Game.GameObjects.Mobile mob in Client.Game.UO.World.Mobiles.Values)
            {
                if (ReferenceEquals(mob, Client.Game.UO.World.Player))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(GUO.IO.Audio.VoiceManager.ResolveRule(mob.Serial, mob.Name, mob.Graphic)))
                {
                    continue;
                }

                int d = System.Math.Max(System.Math.Abs(mob.X - Client.Game.UO.World.Player.X),
                    System.Math.Abs(mob.Y - Client.Game.UO.World.Player.Y));
                if (d < candidateDist)
                {
                    candidateDist = d;
                    candidate = mob;
                }
            }

            Check("an unmapped mobile is nearby", candidate != null,
                candidate == null ? "none" : $"{candidate.Name} body {candidate.Graphic} at {candidateDist}");
            if (candidate != null)
            {
                GUO.IO.Audio.VoiceManager.DesignVoice((int)candidate.Serial, candidate.Name,
                    candidate.Graphic, candidate.GetType().Name);
                bool designed = false;
                for (int i = 0; i < 1200 && !designed; i++)
                {
                    await Frames(host, 30);
                    string st = GUO.IO.Audio.VoiceManager.Status;
                    designed = st.StartsWith("designed");
                    if (st.StartsWith("error"))
                    {
                        break;
                    }
                }

                Check("voice designed for the mobile", designed, GUO.IO.Audio.VoiceManager.Status);
            }
        }

        // Deterministic mob-path proof: deliver a line as the nearest mapped
        // mobile through the real HandleMessage path (packet delivery itself
        // is upstream's, proven by every chat line ever displayed).
        if (!string.IsNullOrWhiteSpace(WalkToVoiceFake))
        {
            Game.GameObjects.Mobile best = null;
            int bestDist = int.MaxValue;
            foreach (Game.GameObjects.Mobile mob in Client.Game.UO.World.Mobiles.Values)
            {
                if (ReferenceEquals(mob, Client.Game.UO.World.Player))
                {
                    continue;
                }

                string voice = GUO.IO.Audio.VoiceManager.ResolveVoice(mob.Serial, mob.Name, mob.Graphic);
                if (string.IsNullOrWhiteSpace(voice))
                {
                    continue;
                }

                int d = System.Math.Max(System.Math.Abs(mob.X - Client.Game.UO.World.Player.X),
                    System.Math.Abs(mob.Y - Client.Game.UO.World.Player.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = mob;
                }
            }

            Check("a mapped mobile is nearby", best != null, best == null ? "none" : $"{best.Name} body {best.Graphic} at {bestDist}");
            if (best != null)
            {
                int heard = GUO.IO.Audio.VoiceManager.MobSpeaks;
                Client.Game.UO.World.MessageManager.HandleMessage(best, WalkToVoiceFake, best.Name,
                    0x03B2, Game.Data.MessageType.Regular, 3, Game.Data.TextType.OBJECT, true, "ENU");
                for (int i = 0; i < 120 && GUO.IO.Audio.VoiceManager.MobSpeaks == heard; i++)
                {
                    await Frames(host, 30);
                }

                heard = GUO.IO.Audio.VoiceManager.MobSpeaks - heard;
                Check("mob line voiced", heard > 0, $"{heard} voiced");
                for (int i = 0; i < 120; i++)
                {
                    await Frames(host, 30);
                    string st = GUO.IO.Audio.VoiceManager.Status;
                    if (st.StartsWith("playing") || st.StartsWith("ready") || st.StartsWith("done"))
                    {
                        break;
                    }

                    if (st.StartsWith("error"))
                    {
                        break;
                    }
                }

                Check("mob voice played", GUO.IO.Audio.VoiceManager.Status.StartsWith("playing")
                    || GUO.IO.Audio.VoiceManager.Status.StartsWith("ready")
                    || GUO.IO.Audio.VoiceManager.Status.StartsWith("done"),
                    GUO.IO.Audio.VoiceManager.Status);
            }
        }

        // Live mob-voice proof: loiter among mobiles and count voiced lines.
        // Vendors greet on approach, so a bank run usually hears something;
        // silence is reported, not hidden.
        if (WalkToHearSecs > 0)
        {
            int heard = GUO.IO.Audio.VoiceManager.MobSpeaks;
            long until = System.Environment.TickCount64 + WalkToHearSecs * 1000L;
            while (System.Environment.TickCount64 < until
                && GUO.IO.Audio.VoiceManager.MobSpeaks == heard)
            {
                await Frames(host, 30);
            }

            heard = GUO.IO.Audio.VoiceManager.MobSpeaks - heard;
            Check($"mobiles spoke in voice ({WalkToHearSecs}s listen)", heard > 0, $"{heard} voiced");
        }

        // Live picking proof: click the splat's projected centre. Selection
        // must become the splat, and a double click after it must be a safe
        // no-op (unknown types must not crash the use/attack paths).
        if (!string.IsNullOrWhiteSpace(WalkToClickSplat))
        {
            var scene = Client.Game.GetScene<Game.Scenes.GameScene>();
            Vector2 at = scene == null ? new Vector2(-1, -1) : scene.SplatClickAt(WalkToClickSplat);
            Check($"splat {WalkToClickSplat} has a click point", at.X >= 0 && at.Y >= 0, $"{at}");
            if (at.X >= 0 && at.Y >= 0)
            {
                // Closed-loop: the camera can drift under a parked mouse
                // (peek), so re-aim at the splat's fresh projection until it
                // selects or time runs out.
                scene.Camera.PeekingToMouse = false;
                GD.Print("[GUO] splat click: hands off the mouse for ~30 s; "
                    + "the real cursor fights the synthetic one and the miss reads as a failure");
                bool picked = false, tracked = false;
                for (int i = 0; i < 10 && !picked; i++)
                {
                    at = scene.SplatClickAt(WalkToClickSplat);
                    await ClickWorld(host, at);
                    await Frames(host, 10);
                    var tm = Game.SelectedObject.TranslatedMousePositionByViewport;
                    var center = scene.SplatTileCenter(WalkToClickSplat);
                    tracked = System.Math.Abs(tm.X - center.X) + System.Math.Abs(tm.Y - center.Y) <= 6;
                    picked = tracked && Game.SelectedObject.Object is Game.GameObjects.SplatObject;
                    GD.Print($"[GUO] splat click {i}: at {at}, mouse-tile {tm.X},{tm.Y} "
                        + $"vs tile {center.X},{center.Y}, tracked={tracked}, "
                        + $"selected {Game.SelectedObject.Object?.GetType().Name ?? "nothing"}");
                }

                Check("the mouse tracks the synthetic click", tracked,
                    "the real cursor moved during the check; hands off and rerun");

                Check($"click selects the splat", picked,
                    Game.SelectedObject.Object?.GetType().Name ?? "nothing selected");
                await DoubleClickWorld(host, at);
                await Frames(host, 30);
                Check("double click on a splat is safe",
                    Client.Game.UO.World.InGame && Client.Game.UO.World.Player != null, "left the world");
            }
        }

        GD.Print($"[GUO] window: mode {Godot.DisplayServer.WindowGetMode()}, "
            + $"pos {Godot.DisplayServer.WindowGetPosition()}, size {Godot.DisplayServer.WindowGetSize()}, "
            + $"focused {Godot.DisplayServer.WindowIsFocused()}, "
            + $"screens {Godot.DisplayServer.GetScreenCount()}");
        Check("arrived at the walk-to target", dist <= 2, $"{player.X},{player.Y} vs {gx},{gy}");

        int passed = 0;
        foreach ((string _, bool ok) in Checks)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                GD.PrintErr("[GUO] probe FAILED (walk-to)");
            }
        }

        Passed = passed == Checks.Count;
        GD.Print($"[GUO] input probe: {passed}/{Checks.Count} checks passed");
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

        GD.Print("[GUO] input probe: equipping a weapon");

        await EquipAWeapon(host);

        GD.Print("[GUO] input probe: clicking someone else");

        await ClickSomeoneElse(host);

        GD.Print("[GUO] input probe: going shopping");

        await VisitAVendor(host);

        GD.Print("[GUO] input probe: trading with a second player");

        await TradeWithAPartner(host);

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

        GD.Print("[GUO] input probe: attacking");

        await AttackSomething(host);

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

        GD.Print("[GUO] input probe: resizing the window");

        await ResizeTheWindow(host);
        await LoseTheWindow(host);

        await Endure(host, EndureSeconds);

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

        // Click aims relative to the login gump (Send adds its origin); the
        // button's screen coordinates already include it.
        Compat.Point origin = LoginOrigin();

        await Click(
            host,
            new Vector2(
                button.ScreenCoordinateX + button.Width / 2f - origin.X,
                button.ScreenCoordinateY + button.Height / 2f - origin.Y
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

        (Game.GameObjects.Item Item, Vector2 At) item = pack == null
            ? default
            : await Grabbable(
                host,
                pack,
                Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.Backpack)
            );

        if (item.Item == null)
        {
            GD.Print("[GUO] input probe: nothing in the backpack can be picked up");

            return;
        }

        Vector2 from = item.At;

        // Bare container, not a fixed offset from where the item was. Down
        // and to the right is empty backpack until the day it is not: this
        // run dropped a book forty pixels onto a bag, the server put the book
        // in the bag -- which is a correct move, and a real player's mistake
        // -- and the check that the book is still in the backpack failed on a
        // client that had done nothing wrong.
        Vector2 to = await EmptySpot(host, pack);

        GD.Print(
            $"[GUO] input probe: dragging {item.Item.Name} 0x{item.Item.Serial:X} from "
            + $"{from.X},{from.Y} to {to.X},{to.Y}"
        );

        // Which item moved is read back from the press, not assumed. Items in
        // a container overlap, so the solid pixel aimed at can belong to the
        // item drawn over the one it was picked from, and then the drag is
        // still a real drag -- of a different item.
        Game.UI.Controls.Control pressed = await Drag(host, from, to);
        uint serial = pressed?.LocalSerial ?? item.Item.Serial;

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
    internal static async System.Threading.Tasks.Task<Vector2?> FindCharacter(Node host)
    {
        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;
        var centre = new Vector2(bounds.Width / 2f, bounds.Height / 2f);

        // On a device the window is not the world: the Thor's holds the
        // second screen's shelf beside it, and its middle is the seam. The
        // character is drawn at the middle of the world view instead.
        Configuration.Profile view = Configuration.ProfileManager.CurrentProfile;

        if (PointerScale != 1f && view != null)
        {
            centre = new Vector2(
                view.GameWindowPosition.X + view.GameWindowSize.X / 2f,
                view.GameWindowPosition.Y + view.GameWindowSize.Y / 2f
            );
        }

        // A column and a little either side of it. The character is drawn
        // at the middle of the window, but not to the pixel -- the camera
        // lags a step, and standing among other people its own sprite can be
        // overdrawn -- so a bare column reported "not on screen" for a
        // character that was plainly there.
        int[] across = { 0, -12, 12, -24, 24 };

        for (int dy = 0; dy >= -120; dy -= 10)
        {
            foreach (int dx in across)
            {
                var at = new Vector2(centre.X + dx, centre.Y + dy);

                Send(new InputEventMouseMotion { Position = at });

                await Frames(host, 4);

                if (ReferenceEquals(Game.SelectedObject.Object, Client.Game.UO.World.Player))
                {
                    return at;
                }
            }
        }

        // Sixty-five points said no. A run that only says "not on screen"
        // leaves nothing to work from, so it says what did answer at the
        // middle of the window instead, and whether a gump had the mouse at
        // all -- a gump over the centre stops the world being asked.
        Send(new InputEventMouseMotion { Position = centre });

        await Frames(host, 4);

        GD.Print(
            $"[GUO] input probe: no character at {centre}; "
                + $"over {Game.SelectedObject.Object?.GetType().Name ?? "nothing"} "
                + $"0x{(Game.SelectedObject.Object as Game.GameObjects.Entity)?.Serial ?? 0:X}, "
                + $"gump {Game.Managers.UIManager.MouseOverControl?.GetType().Name ?? "none"}"
                + $" in {Game.Managers.UIManager.MouseOverControl?.RootParent?.GetType().Name ?? "none"}"
                + $" at {Game.Managers.UIManager.MouseOverControl?.ScreenCoordinateX},{Game.Managers.UIManager.MouseOverControl?.ScreenCoordinateY}"
                + $" {Game.Managers.UIManager.MouseOverControl?.Width}x{Game.Managers.UIManager.MouseOverControl?.Height}, "
                + $"player {(Client.Game.UO.World.Player == null ? "gone" : "here")}"
        );

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

        // Anatomy by preference, then the other two that ask about a
        // person: what a skill will accept as a target is the skill's own
        // business, and clicking a character at an Arms Lore cursor -- which
        // wants an item -- leaves the cursor up and reads as a client that
        // cannot target. Any other usable skill would still exercise the
        // gump; not all of them raise a cursor at all.
        string[] aboutPeople = { "Anatomy", "Evaluating Intelligence", "Forensic Evaluation" };

        Game.UI.Controls.Button use = null;

        foreach (string wanted in aboutPeople)
        {
            foreach (Game.UI.Controls.Button candidate in uses)
            {
                if (SkillNameOf(candidate) == wanted)
                {
                    use = candidate;

                    break;
                }
            }

            if (use != null)
            {
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

        // The backpack, if the cursor is still up. A skill the probe did not
        // choose may want an item rather than a person -- "what item do you
        // wish to get information about?" -- and the path being checked is
        // that a target cursor sends what it was pointed at and the server
        // takes it, which either answer proves.
        if (Client.Game.UO.World.TargetManager.IsTargeting)
        {
            Game.UI.Gumps.ContainerGump pack =
                Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

            (Game.GameObjects.Item Item, Vector2 At) thing = pack == null
                ? default
                : await Grabbable(
                    host,
                    pack,
                    Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.Backpack)
                );

            if (thing.Item != null)
            {
                GD.Print($"[GUO] input probe: targeting {thing.Item.Name} instead");

                await Click(host, thing.At);

                await Frames(host, 90);
            }
        }

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

            // Found again right before the click: townspeople walk, and a
            // position worked out a second ago puts the click on the floor
            // they were standing on -- which the server answers, politely,
            // with "marble floor".
            Vector2? on = await FindOnScreen(host, mobile, self.Value);

            if (on == null)
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

            await Click(host, on.Value);

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

            // The same click also asks for a context menu, and one may well
            // arrive -- but whether it does is the server's decision, so the
            // check for it is made of a shopkeeper instead, in VisitAVendor.
            //
            // Anything that did arrive is closed again, and this is not
            // tidiness. A popup sits over the world where the mouse was, and
            // while it is there the hit test answers with the gump and not
            // with what is drawn underneath -- so the next step that goes
            // looking for a mobile on screen finds nothing, which is a
            // confusing way to fail a trade.
            await Frames(host, 30);

            Game.Managers.UIManager.ShowGamePopup(null);

            await Frames(host, 10);

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

    /// <summary>
    /// Take what is in the character's hand off, and put a weapon on.
    /// </summary>
    /// <remarks>
    /// Both halves of wearing things, and the order matters: the paperdoll
    /// only equips onto a layer that is empty -- upstream checks exactly that
    /// -- so the book the character starts holding has to be dragged into the
    /// backpack before a sword can go in the same hand. That is what a player
    /// does too.
    ///
    /// Double-clicking a weapon does not wield it, which is worth writing
    /// down because it looks like it should: in this ruleset double-clicking
    /// a blade asks what to carve with it, which is why an early version of
    /// this left a target cursor up and broke every check after it.
    /// </remarks>
    private static async System.Threading.Tasks.Task EquipAWeapon(Node host)
    {
        Game.UI.Gumps.ContainerGump pack =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

        Game.UI.Gumps.PaperDollGump doll =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.PaperDollGump>();

        if (pack == null || doll == null)
        {
            Check("the hands can be emptied", false, "the backpack or the paperdoll is not open");

            return;
        }

        // Clear of the paperdoll it is about to trade items with. The
        // container opens at the client's default spot, which is underneath
        // the paperdoll, and a press there lands on an equipment slot.
        pack.X = 1400;
        pack.Y = 200;

        await Frames(host, 20);

        Vector2 intoPack = await EmptySpot(host, pack);

        // Both hands, not just the one with a weapon in it. A shield lives on
        // the two-handed layer and stays there while a sword comes off the
        // other; the drop that follows then lands on the shield's picture and
        // is ignored, because the paperdoll only wears onto a layer that is
        // empty.
        Game.Data.Layer[] hands = { Game.Data.Layer.OneHanded, Game.Data.Layer.TwoHanded };

        foreach (Game.Data.Layer hand in hands)
        {
            Game.GameObjects.Item worn = Client.Game.UO.World.Player.FindItemByLayer(hand);

            if (worn == null)
            {
                continue;
            }

            Game.UI.Controls.Control picture = FindControl(doll, worn.Serial);

            if (picture == null)
            {
                continue;
            }

            GD.Print($"[GUO] input probe: taking off {worn.Name} 0x{worn.Serial:X}");

            // The solid part of the art, not the middle of its box: a
            // katana is a thin diagonal and the middle of its rectangle is
            // the paperdoll behind it.
            await Drag(host, GrabPoint(picture), intoPack);

            await Frames(host, 120);
        }

        Game.GameObjects.Item left =
            Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.OneHanded)
            ?? Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.TwoHanded);

        Check(
            "the hands can be emptied",
            left == null,
            left == null ? "both hands are free" : $"still holding {left.Name} 0x{left.Serial:X}"
        );

        Game.GameObjects.Item weapon = FindWeapon();

        if (weapon == null)
        {
            Check("a weapon can be equipped", false, "there is no weapon in the backpack");

            return;
        }

        Game.UI.Controls.ItemGump icon = FindItem(pack, weapon.Serial);

        Vector2? grab = icon == null ? null : await PressPoint(host, icon);

        if (grab == null)
        {
            Check(
                "a weapon can be equipped",
                false,
                icon == null
                    ? $"0x{weapon.Serial:X} is not drawn"
                    : $"{weapon.Name} is drawn under something else"
            );

            return;
        }

        GD.Print($"[GUO] input probe: equipping {weapon.Name} 0x{weapon.Serial:X}");

        await Drag(
            host,
            grab.Value,
            new Vector2(
                doll.ScreenCoordinateX + doll.Width / 2f,
                doll.ScreenCoordinateY + doll.Height / 2f
            )
        );

        await Frames(host, 120);

        Game.GameObjects.Item held =
            Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.OneHanded)
            ?? Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.TwoHanded);

        Check(
            "a weapon can be equipped",
            held != null && held.Serial == weapon.Serial,
            held == null ? "nothing is in hand" : $"holding {held.Name} 0x{held.Serial:X}"
        );

        // Never leave the run holding something: an item on the cursor
        // swallows every click after it.
        if (Client.Game.UO.GameCursor.ItemHold.Enabled)
        {
            await Click(host, intoPack);

            await Frames(host, 60);

            GD.Print("[GUO] input probe: put the held item back in the backpack");
        }
    }

    /// <summary>
    /// A point inside an open container where no item is drawn.
    /// </summary>
    /// <remarks>
    /// Dropping onto another item is a different gesture: the client asks the
    /// server to put the held item into or onto that one, and a shard that
    /// will not put a katana inside a spellbook simply refuses. The refusal is
    /// quiet -- the cursor empties, the journal says nothing, and the item is
    /// still worn -- which is a convincing impression of a broken drag until
    /// you notice what the drop landed on.
    /// </remarks>
    private static async System.Threading.Tasks.Task<Vector2> EmptySpot(
        Node host,
        Game.UI.Gumps.ContainerGump pack
    )
    {
        for (float y = 0.25f; y <= 0.8f; y += 0.15f)
        {
            for (float x = 0.15f; x <= 0.9f; x += 0.15f)
            {
                var at = new Vector2(
                    pack.ScreenCoordinateX + pack.Width * x,
                    pack.ScreenCoordinateY + pack.Height * y
                );

                Send(new InputEventMouseMotion { Position = at });

                await Frames(host, 2);

                Game.UI.Controls.Control under = Game.Managers.UIManager.MouseOverControl;

                if (under is Game.UI.Controls.ItemGump)
                {
                    continue;
                }

                if (ReferenceEquals(under, pack) || ReferenceEquals(under?.RootParent, pack))
                {
                    return at;
                }
            }
        }

        // Nowhere bare: the middle of it, and the drop will say why.
        return new Vector2(
            pack.ScreenCoordinateX + pack.Width / 2f,
            pack.ScreenCoordinateY + pack.Height / 2f
        );
    }

    /// <summary>
    /// Walk to a shopkeeper with the client's own pathfinder and buy something.
    /// </summary>
    /// <remarks>
    /// The longest round trip in the client that is not combat, and the first
    /// one here that needs the character to be somewhere in particular: this
    /// ruleset only lets a vendor hear a customer who is standing next to
    /// them, so the probe asks Pathfinder to take it there -- a walk it does
    /// not steer, a step at a time, the way a click on the ground does.
    ///
    /// "vendor buy" rather than "buy" because the short form has to be
    /// addressed by name, and the probe would then have to know which of the
    /// townspeople standing around is a shopkeeper. It does not: it walks up
    /// to the nearest few and asks, and the one that sells something answers
    /// with a shop.
    ///
    /// Buying is two gestures on that shop -- double-click an item to put it
    /// in the basket, then Accept -- and the evidence is the gold, which only
    /// the server can change.
    /// </remarks>
    private static async System.Threading.Tasks.Task VisitAVendor(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        var townspeople = new System.Collections.Generic.List<Game.GameObjects.Mobile>();

        foreach (Game.GameObjects.Mobile mobile in Client.Game.UO.World.Mobiles.Values)
        {
            if (mobile.Serial != player.Serial
                && mobile.NotorietyFlag == Game.Data.NotorietyFlag.Invulnerable)
            {
                townspeople.Add(mobile);
            }
        }

        // Far enough away that walking there means something: a run that
        // began standing on top of a shopkeeper -- which happens, the
        // character logs back in where it left off -- "walked" nought tiles
        // and failed a check about the pathfinder for no good reason.
        townspeople.RemoveAll(m => Distance(m) < 3);

        townspeople.Sort((a, b) => Distance(a).CompareTo(Distance(b)));

        bool walked = false;
        Game.UI.Gumps.ShopGump shop = null;
        Game.GameObjects.Mobile shopkeeper = null;

        for (int i = 0; i < townspeople.Count && i < 3 && shop == null; i++)
        {
            Game.GameObjects.Mobile person = townspeople[i];

            int before = Distance(person);

            if (!player.Pathfinder.WalkTo(person.X, person.Y, person.Z, 1))
            {
                GD.Print($"[GUO] input probe: no path to {person.Name}");

                continue;
            }

            // Autowalk is stepped by the game scene, so this only waits.
            for (int wait = 0; wait < 120 && Distance(person) > 1; wait++)
            {
                await Frames(host, 10);

                if (!player.Pathfinder.AutoWalking)
                {
                    break;
                }
            }

            player.Pathfinder.StopAutoWalk();

            int after = Distance(person);

            GD.Print(
                $"[GUO] input probe: walked to {person.Name}, {before} tiles away and now {after}"
            );

            walked |= after < before && after <= 2;

            if (after > 1)
            {
                continue;
            }

            await Say(host, "vendor buy");

            for (int wait = 0; wait < 30 && shop == null; wait++)
            {
                await Frames(host, 10);

                shop = Game.Managers.UIManager.GetGump<Game.UI.Gumps.ShopGump>();
            }

            if (shop != null)
            {
                shopkeeper = person;
            }
        }

        Check("the pathfinder walks the character to someone", walked);

        if (shop == null)
        {
            Check("a shopkeeper opens a shop", false, "nobody nearby is selling anything");

            return;
        }

        Check("a shopkeeper opens a shop", true, $"{shop.Width}x{shop.Height}");

        await AskForAMenu(host, shopkeeper);

        // Clear of the paperdoll and the backpack, both of which the shop
        // opens on top of and which would otherwise take the clicks.
        shop.X = 200;
        shop.Y = 400;

        await Frames(host, 20);

        Game.UI.Controls.Control item = FirstShopItem(shop);
        Game.UI.Controls.Control accept = null;

        foreach (Game.UI.Controls.Control child in shop.Children)
        {
            if (child is Game.UI.Controls.HitBox && (child.Tooltip as string) == "Accept")
            {
                accept = child;

                break;
            }
        }

        if (item == null || accept == null)
        {
            Check(
                "the shard sells something",
                false,
                item == null ? "the shop is empty" : "the shop has no Accept button"
            );

            return;
        }

        uint purse = player.Gold;

        var had = new System.Collections.Generic.HashSet<uint>();

        Game.GameObjects.Item bag = player.FindItemByLayer(Game.Data.Layer.Backpack);

        for (Game.LinkedObject i = bag?.Items; i != null; i = i.Next)
        {
            had.Add(((Game.GameObjects.Item)i).Serial);
        }

        await DoubleClick(host, Centre(item));

        await Frames(host, 30);

        await Click(host, Centre(accept));

        Game.GameObjects.Item bought = null;

        for (int wait = 0; wait < 30 && bought == null && player.Gold == purse; wait++)
        {
            await Frames(host, 10);

            bag = player.FindItemByLayer(Game.Data.Layer.Backpack);

            for (Game.LinkedObject i = bag?.Items; i != null; i = i.Next)
            {
                var it = (Game.GameObjects.Item)i;

                if (!had.Contains(it.Serial))
                {
                    bought = it;

                    break;
                }
            }
        }

        // The gold is the obvious evidence and it is the wrong one here: the
        // probe's account owns the shard, and a shopkeeper will not charge a
        // Game Master -- "I would not presume to charge thee anything". What
        // the purchase actually produces either way is a thing in the pack
        // that was not there before.
        Check(
            "the shard sells something",
            bought != null || player.Gold < purse,
            bought == null
                ? $"{purse} gold before, {player.Gold} after, and nothing new in the pack"
                : $"{bought.Name} arrived, gold {purse} -> {player.Gold}"
        );
    }

    /// <summary>
    /// Trade an item with another player, who is a second client the probe
    /// starts for the purpose.
    /// </summary>
    /// <remarks>
    /// The last thing in the protocol a lone client cannot reach. A secure
    /// trade is opened by the server for two players at once: dropping an
    /// item on somebody is a request, the shard answers both clients with a
    /// trade window, and the goods only change hands when both have ticked
    /// their side. A shopkeeper will not do it and neither will a horse, so
    /// there has to be a second player -- hence a second copy of the client,
    /// started here and killed when this is done with it. TradePartner is the
    /// other half.
    ///
    /// What is checked is both ends of that: a window that only the server
    /// can open, and an item that leaves this character's backpack because
    /// the other one accepted.
    /// </remarks>
    private static async System.Threading.Tasks.Task TradeWithAPartner(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        // Where to come back to. The trade happens wherever the other client
        // logged in, which is somebody's front room, and the steps after this
        // one need a character that can be seen: standing indoors under a
        // roof, the hit test finds floor where the character is.
        int homeX = player.X;
        int homeY = player.Y;
        int homeZ = player.Z;

        // Stale coordinates would send the probe to where the last run's
        // partner stood, so the note is torn up before the new one starts.
        if (FileAccess.FileExists(TradePartner.WhereIAm))
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(TradePartner.WhereIAm));
        }

        string[] arguments =
        {
            "--path",
            ProjectSettings.GlobalizePath("res://"),
            "--",
            "--play",
            "--trade-partner"
        };

        int partnerProcess = OS.CreateProcess(OS.GetExecutablePath(), arguments);

        if (partnerProcess <= 0)
        {
            Check("a second player arrives", false, "the second client would not start");

            return;
        }

        GD.Print($"[GUO] input probe: started a second client, pid {partnerProcess}");

        try
        {
            // Where it ended up, which is not where this character is: the
            // two were made on the same shard and came up forty tiles apart.
            string note = null;

            for (int wait = 0; wait < 240 && note == null; wait++)
            {
                await Frames(host, 30);

                if (!FileAccess.FileExists(TradePartner.WhereIAm))
                {
                    continue;
                }

                using FileAccess read = FileAccess.Open(
                    TradePartner.WhereIAm,
                    FileAccess.ModeFlags.Read
                );

                note = read?.GetLine();
            }

            if (string.IsNullOrWhiteSpace(note))
            {
                Check("a second player arrives", false, "the second client never logged in");

                return;
            }

            GD.Print($"[GUO] input probe: the second player is at {note}");

            string[] where = note.Trim().Split(' ');

            if (where.Length != 3 || !int.TryParse(where[0], out int wx))
            {
                Check("a second player arrives", false, $"cannot read \"{note}\"");

                return;
            }

            // The probe owns this shard, so it can simply be there. Walking
            // forty tiles of town is a pathfinder test, and one bad step in it
            // would fail the trade check for the wrong reason.
            //
            if (!int.TryParse(where[1], out int wy))
            {
                Check("a second player arrives", false, $"cannot read \"{note}\"");

                return;
            }

            // Two tiles off, not one and not none. Two mobiles on the same
            // tile are drawn in the same place and the hit test answers with
            // this character every time; one tile apart their sprites still
            // overlap and which of them a pixel belongs to changes as they
            // breathe. Two is clear of that and still inside the three tiles
            // an item can be handed across.
            await Say(host, $"[go {wx + 2} {wy} {where[2]}");

            Game.GameObjects.Mobile partner = null;

            // Long: the second client loads the whole install again before it
            // can log in, and it is doing that while this one is playing.
            for (int wait = 0; wait < 240 && partner == null; wait++)
            {
                await Frames(host, 30);

                foreach (Game.GameObjects.Mobile mobile in Client.Game.UO.World.Mobiles.Values)
                {
                    // Blue and not this character, which is nearly the other
                    // player: most townspeople are invulnerable, but not all
                    // of them are -- the first innocent mobile in New Haven
                    // turned out to be a wandering healer. So the name
                    // decides, and the name has to be asked for: the client
                    // only knows the ones it has clicked, and a mobile that
                    // has just walked into view has none.
                    if (mobile.Serial == player.Serial
                        || mobile.NotorietyFlag != Game.Data.NotorietyFlag.Innocent)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(mobile.Name))
                    {
                        Game.GameActions.SingleClick(Client.Game.UO.World, mobile.Serial);

                        continue;
                    }

                    if (mobile.Name == TradePartner.PartnerCharacter)
                    {
                        partner = mobile;

                        break;
                    }
                }
            }

            if (partner == null)
            {
                Check("a second player arrives", false, "nobody else logged in");

                return;
            }

            Check(
                "a second player arrives",
                true,
                $"{(string.IsNullOrEmpty(partner.Name) ? "unnamed" : partner.Name)} "
                    + $"0x{partner.Serial:X}, {Distance(partner)} tiles away"
            );

            Game.UI.Gumps.ContainerGump pack =
                Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

            Game.GameObjects.Item bag = player.FindItemByLayer(Game.Data.Layer.Backpack);

            // Round them, if need be. Two tiles east is usually a clear view
            // of somebody, but not when there is a wall or a cupboard in
            // between, and a partner that cannot be seen cannot be handed
            // anything -- so the probe tries the other sides before giving up.
            (int X, int Y)[] sides = { (2, 0), (0, 2), (-2, 0), (0, -2), (1, 1) };

            (Game.GameObjects.Item Item, Vector2 At) offer =
                pack == null ? default : await Grabbable(host, pack, bag);

            if (offer.Item == null)
            {
                Check("the shard opens a trade", false, "nothing in the backpack can be picked up");

                return;
            }

            GD.Print(
                $"[GUO] input probe: offering {offer.Item.Name} 0x{offer.Item.Serial:X} "
                    + $"to {partner.Name}"
            );

            // Lifted first and aimed afterwards, which is the opposite of
            // every other drag here. A drag that works out where to let go
            // before it picks anything up lets go a second later, and a
            // second is long enough for two people standing next to each
            // other to shuffle: the item was dropped on the ground, or on the
            // character offering it, while the hit test had been right when
            // it was asked.
            //
            // Twice from each side, and then the other side. Aiming again
            // covers the frame the sprites swap on; it does not cover a spot
            // where the partner can only just be seen at all, and one run
            // found exactly that -- a pixel that answered with the partner
            // before the item was lifted and with the ground afterwards,
            // three times running. A player who cannot hand something over
            // walks round; so does this.
            Game.UI.Gumps.TradingGump trade = null;
            Vector2? self = null;

            for (int side = 0; side < sides.Length && trade == null; side++)
            {
                if (side > 0)
                {
                    GD.Print($"[GUO] input probe: trying the next side of {partner.Name}");

                    await Say(host, $"[go {wx + sides[side].X} {wy + sides[side].Y} {where[2]}");

                    await Frames(host, 90);
                }

                self = await FindCharacter(host);

                Vector2? on = self == null
                    ? null
                    : await FindOnScreen(host, partner, self.Value);

                if (on == null)
                {
                    continue;
                }

                for (int go = 0; go < 2 && trade == null; go++)
                {
                    if (!Client.Game.UO.GameCursor.ItemHold.Enabled
                        && !await Lift(host, offer.At))
                    {
                        Check(
                            "the shard opens a trade",
                            false,
                            "the offer would not come off the shelf"
                        );

                        return;
                    }

                    Vector2? aim = await FindOnScreen(host, partner, self.Value, holding: true);

                    // What the client thinks the cursor is over at the moment
                    // the button comes up, which is the thing that decides
                    // where the item goes. Printed because a refused drop says
                    // nothing at all, and a run that fails silently three
                    // times leaves nothing to work from.
                    GD.Print(
                        $"[GUO] input probe: letting go at {(aim?.ToString() ?? "the old aim")}, "
                        + $"over {Game.SelectedObject.Object?.GetType().Name ?? "nothing"} "
                        + $"0x{(Game.SelectedObject.Object as Game.GameObjects.Entity)?.Serial ?? 0:X}, "
                        + $"holding {Client.Game.UO.GameCursor.ItemHold.Enabled}, "
                        + $"{Distance(partner)} tiles away"
                    );

                    await Release(host, aim ?? on.Value);

                    for (int wait = 0; wait < 40 && trade == null; wait++)
                    {
                        await Frames(host, 10);

                        trade = Game.Managers.UIManager.GetGump<Game.UI.Gumps.TradingGump>();
                    }

                    if (trade == null)
                    {
                        GD.Print("[GUO] input probe: the offer was not taken; aiming again");
                    }
                }
            }

            Check(
                "the shard opens a trade",
                trade != null,
                trade == null
                    ? $"{partner.Name} would not take it from any side"
                    : $"with {partner.Name}"
            );

            if (trade == null)
            {
                return;
            }

            // Clear of everything else, then tick this side's box. The other
            // side ticks its own, and the server moves the goods.
            trade.X = 1200;
            trade.Y = 700;

            await Frames(host, 20);

            Game.UI.Controls.Control box = FirstOfType(trade, "Checkbox");

            if (box == null)
            {
                Check("the goods change hands", false, "the trade window has no checkbox");

                return;
            }

            await Click(host, Centre(box));

            bool gone = false;

            for (int wait = 0; wait < 60 && !gone; wait++)
            {
                await Frames(host, 10);

                gone = !Client.Game.UO.World.Items.TryGetValue(
                        offer.Item.Serial,
                        out Game.GameObjects.Item still
                    )
                    || still.Container != bag.Serial;
            }

            Check(
                "the goods change hands",
                gone,
                gone
                    ? $"{offer.Item.Name} is no longer in the pack"
                    : $"{offer.Item.Name} came back"
            );
        }
        finally
        {
            // Whatever happened, do not walk away holding it: an item on the
            // cursor swallows every click for the rest of the run, which is
            // how one failed offer turned into four failed checks.
            if (Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                Game.UI.Gumps.ContainerGump back =
                    Game.Managers.UIManager.GetGump<Game.UI.Gumps.ContainerGump>();

                if (back != null)
                {
                    await Click(host, await EmptySpot(host, back));

                    await Frames(host, 60);
                }

                GD.Print("[GUO] input probe: put the offered item back");
            }

            OS.Kill(partnerProcess);

            GD.Print("[GUO] input probe: the second client is closed");

            if (player.X != homeX || player.Y != homeY)
            {
                await Say(host, $"[go {homeX} {homeY} {homeZ}");

                await Frames(host, 60);
            }
        }
    }

    /// <summary>
    /// A point on this control that the client agrees is this control.
    /// </summary>
    /// <remarks>
    /// Icons in a container overlap, and a pixel that is solid in one item's
    /// art can belong to the item drawn over it. Aiming at the middle of the
    /// art and hoping is how a katana's icon was pressed and a shield came
    /// off the pile instead -- and the shield, dropped on the paperdoll, is
    /// what the character ended up wearing. So the point is hovered and the
    /// client asked, and the search widens over the icon until it agrees.
    /// </remarks>
    private static async System.Threading.Tasks.Task<Vector2?> PressPoint(
        Node host,
        Game.UI.Controls.Control icon
    )
    {
        var candidates = new System.Collections.Generic.List<Vector2> { GrabPoint(icon) };

        for (int y = 3; y < icon.Height; y += 5)
        {
            for (int x = 3; x < icon.Width; x += 5)
            {
                candidates.Add(new Vector2(icon.ScreenCoordinateX + x, icon.ScreenCoordinateY + y));
            }
        }

        foreach (Vector2 at in candidates)
        {
            Send(new InputEventMouseMotion { Position = at });

            await Frames(host, 4);

            if (ReferenceEquals(Game.Managers.UIManager.MouseOverControl, icon))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>
    /// Ask for somebody's context menu, and wait for the one the server
    /// composes.
    /// </summary>
    /// <remarks>
    /// A shopkeeper, not a passer-by: the client asks for a context menu on
    /// every single click, but the server only answers when the thing clicked
    /// has something to offer, and a townsperson standing in the road often
    /// has nothing. That failed the check on a client that had asked properly
    /// and been told, correctly, that there was no menu.
    ///
    /// Through the same call the click path makes, rather than with the mouse,
    /// and that is a deliberate step back. By the time the probe knows who the
    /// shopkeeper is it is standing next to them, and two characters a tile
    /// apart overlap: the hit test hands back whichever of them owns the pixel
    /// this frame, which is how "Ambar is not on screen" came to fail a run in
    /// which Ambar had just sold something. What is being checked here is that
    /// the shard composes a menu and the client puts it on the screen; that a
    /// single click asks for one is already covered, by the name that comes
    /// back from the same click in ClickSomeoneElse.
    /// </remarks>
    private static async System.Threading.Tasks.Task AskForAMenu(
        Node host,
        Game.GameObjects.Mobile person
    )
    {
        if (person == null)
        {
            Check("the shard offers a context menu", false, "nobody to ask");

            return;
        }

        Game.GameActions.OpenPopupMenu(person.Serial, true);

        Game.UI.Gumps.PopupMenuGump menu = null;

        for (int wait = 0; wait < 20 && menu == null; wait++)
        {
            await Frames(host, 10);

            menu = Game.Managers.UIManager.PopupMenu;
        }

        Check(
            "the shard offers a context menu",
            menu != null,
            menu == null ? "no popup arrived" : $"{menu.Width}x{menu.Height} from {person.Name}"
        );

        // Off the screen again before anything else is clicked: a popup over
        // the world takes the hit test with it.
        Game.Managers.UIManager.ShowGamePopup(null);

        await Frames(host, 10);
    }

    /// <summary>Press on something and start moving, so the cursor takes it.</summary>
    private static async System.Threading.Tasks.Task<bool> Lift(Node host, Vector2 at)
    {
        Send(new InputEventMouseMotion { Position = at });

        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = at,
            Pressed = true,
        });

        await Frames(host, 6);

        Vector2 previous = at;

        for (int i = 1; i <= 4; i++)
        {
            Vector2 step = at + new Vector2(i * 6, i * 4);

            Send(new InputEventMouseMotion
            {
                Position = step,
                Relative = step - previous,
                ButtonMask = MouseButtonMask.Left,
            });

            previous = step;

            await Frames(host, 4);
        }

        return Client.Game.UO.GameCursor.ItemHold.Enabled;
    }

    /// <summary>Let go of what the cursor is holding, here.</summary>
    private static async System.Threading.Tasks.Task Release(Node host, Vector2 at)
    {
        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = at,
            Pressed = false,
        });

        await Frames(host, 40);
    }

    /// <summary>
    /// An item in the backpack whose icon the cursor can actually take hold
    /// of, and the point to press.
    /// </summary>
    /// <remarks>
    /// Items in a container overlap, and an icon's own solid pixel can belong
    /// to the icon drawn on top of it: aiming at the first thing in the pack
    /// pressed on a pile of gold instead and dragged nothing. So each
    /// candidate is hovered first and only taken if the client agrees that is
    /// what is under the cursor.
    /// </remarks>
    private static async System.Threading.Tasks.Task<(Game.GameObjects.Item, Vector2)> Grabbable(
        Node host,
        Game.UI.Gumps.ContainerGump pack,
        Game.GameObjects.Item bag
    )
    {
        for (Game.LinkedObject i = bag?.Items; i != null; i = i.Next)
        {
            var item = (Game.GameObjects.Item)i;

            // Not a stack. Pressing on a pile of a thousand gold coins does
            // not pick it up: it asks how many, in a gump of its own, and the
            // cursor stays empty while that is up.
            if (item.Amount > 1)
            {
                continue;
            }

            Game.UI.Controls.ItemGump icon = FindItem(pack, item.Serial);

            if (icon == null)
            {
                continue;
            }

            Vector2? at = await PressPoint(host, icon);

            if (at != null)
            {
                return (item, at.Value);
            }
        }

        return (null, Vector2.Zero);
    }

    /// <summary>The first visible control of this type name under a parent.</summary>
    private static Game.UI.Controls.Control FirstOfType(
        Game.UI.Controls.Control parent,
        string typeName
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.GetType().Name == typeName && child.IsVisible)
            {
                return child;
            }

            Game.UI.Controls.Control found = FirstOfType(child, typeName);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>How far a mobile is from the player, in tiles walked.</summary>
    private static int Distance(Game.GameObjects.Mobile mobile)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        return System.Math.Max(
            System.Math.Abs(mobile.X - player.X),
            System.Math.Abs(mobile.Y - player.Y)
        );
    }

    /// <summary>
    /// The first thing on a shop's shelf that is inside the shop's window.
    /// </summary>
    /// <remarks>
    /// The shelf is a scroll area and it is taller than the frame around it,
    /// so the lines below the fold are laid out, visible and not on screen.
    /// Double-clicking one of those is a double-click on whatever the shop is
    /// drawn over, and the purchase that follows is of nothing at all.
    /// </remarks>
    private static Game.UI.Controls.Control FirstShopItem(Game.UI.Gumps.Gump shop)
    {
        return FirstShopItem(shop, shop);
    }

    private static Game.UI.Controls.Control FirstShopItem(
        Game.UI.Controls.Control parent,
        Game.UI.Gumps.Gump shop
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.GetType().Name == "ShopItem"
                && child.IsVisible
                && child.ScreenCoordinateY >= shop.ScreenCoordinateY
                && child.ScreenCoordinateY + child.Height
                    <= shop.ScreenCoordinateY + shop.Height)
            {
                return child;
            }

            Game.UI.Controls.Control found = FirstShopItem(child, shop);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The first control under <paramref name="parent" /> for this serial.</summary>
    private static Game.UI.Controls.Control FindControl(
        Game.UI.Controls.Control parent,
        uint serial
    )
    {
        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.LocalSerial == serial && child.IsVisible)
            {
                return child;
            }

            Game.UI.Controls.Control found = FindControl(child, serial);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A weapon in the backpack.
    /// </summary>
    /// <remarks>
    /// By the tile data's own weapon flag, not by its layer: a candle is worn
    /// in a hand too, and wielding one proves rather less.
    /// </remarks>
    private static Game.GameObjects.Item FindWeapon()
    {
        Game.GameObjects.Item pack =
            Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.Backpack);

        for (Game.LinkedObject i = pack?.Items; i != null; i = i.Next)
        {
            var item = (Game.GameObjects.Item)i;

            ref Assets.StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[
                item.Graphic
            ];

            // Both: the weapon flag is set on things that are not weapons --
            // a leather jingasa is a hat -- and the hand layers are worn by
            // things that are not weapons either, like a candle.
            // One-handed only. A shield is a two-handed thing with the
            // weapon flag set, and equipping one proves the same round trip
            // while reading, in the log, like a mistake.
            if (data.IsWeapon && data.Layer == (byte)Game.Data.Layer.OneHanded)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Attack the nearest thing it is lawful to attack.
    /// </summary>
    /// <remarks>
    /// Townspeople are invulnerable and attacking an innocent would put a
    /// criminal flag on the character and bring the guards, so the target is
    /// a grey one -- an animal or a monster, which the generated world has
    /// wandering around New Haven.
    ///
    /// What is checked is the server's half. The client sets its own
    /// last-attack the moment it sends the request; the shard answers 0xAA
    /// with the target it accepted, and the client then asks for that
    /// mobile's status -- so a target that comes back with hit points is the
    /// round trip and not an echo of the click.
    /// </remarks>
    private static async System.Threading.Tasks.Task AttackSomething(Node host)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        Vector2? self = await FindCharacter(host);

        if (self == null)
        {
            Check("the shard accepts an attack", false, "the player is not on screen");

            return;
        }

        var quarry = new System.Collections.Generic.List<Game.GameObjects.Mobile>();

        foreach (Game.GameObjects.Mobile mobile in Client.Game.UO.World.Mobiles.Values)
        {
            if (mobile.Serial == player.Serial)
            {
                continue;
            }

            if (mobile.NotorietyFlag == Game.Data.NotorietyFlag.Gray
                || mobile.NotorietyFlag == Game.Data.NotorietyFlag.Criminal
                || mobile.NotorietyFlag == Game.Data.NotorietyFlag.Enemy
                || mobile.NotorietyFlag == Game.Data.NotorietyFlag.Murderer)
            {
                quarry.Add(mobile);
            }
        }

        quarry.Sort(
            (a, b) =>
                (System.Math.Abs(a.X - player.X) + System.Math.Abs(a.Y - player.Y)).CompareTo(
                    System.Math.Abs(b.X - player.X) + System.Math.Abs(b.Y - player.Y)
                )
        );

        int tried = 0;

        foreach (Game.GameObjects.Mobile mobile in quarry)
        {
            if (tried >= 4)
            {
                break;
            }

            Vector2? on = await FindOnScreen(host, mobile, self.Value);

            if (on == null)
            {
                continue;
            }

            tried++;

            GD.Print($"[GUO] input probe: attacking {mobile.Name} 0x{mobile.Serial:X}");

            await DoubleClick(host, on.Value);

            bool accepted = false;

            for (int wait = 0; wait < 8 && !accepted; wait++)
            {
                await Frames(host, 20);

                accepted =
                    Client.Game.UO.World.TargetManager.LastAttack == mobile.Serial
                    && mobile.HitsMax > 0;
            }

            if (!accepted)
            {
                // Refused, and the refusal is a 0xAA naming someone else --
                // usually nobody. Out of range is the common reason, and the
                // next one along is a different distance, so try it.
                GD.Print(
                    $"[GUO] input probe: {mobile.Name} was refused: the shard's last attack is "
                    + $"0x{Client.Game.UO.World.TargetManager.LastAttack:X}, "
                    + $"{mobile.Hits}/{mobile.HitsMax} hit points known"
                );

                continue;
            }

            Check(
                "the shard accepts an attack",
                true,
                $"{mobile.Name} at {mobile.Hits}/{mobile.HitsMax}"
            );

            return;
        }

        Check(
            "the shard accepts an attack",
            false,
            $"{tried} of {quarry.Count} lawful targets were clicked and none was accepted"
        );
    }

    /// <summary>
    /// Where a mobile is drawn, found by asking rather than by working it out.
    /// </summary>
    /// <remarks>
    /// The isometric position of the tile a mobile stands on is easy to work
    /// out from the player's own; where its body is drawn is not. It sits
    /// above that tile by however tall its graphic is, and higher again when
    /// the ground is raised. Both are in the renderer, so the probe sweeps up
    /// the column and asks the client what is under the cursor -- the same hit
    /// test the player's own aim uses.
    /// </remarks>
    private static async System.Threading.Tasks.Task<Vector2?> FindOnScreen(
        Node host,
        Game.GameObjects.Mobile mobile,
        Vector2 self,
        bool holding = false
    )
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World.Player;

        // Sideways as well as up. A mobile standing next to this one is
        // drawn a tile's width away and overlapping it, and the pixel directly
        // above its feet can belong to whoever is drawn in front -- so the
        // column alone found people across the square and missed the one
        // standing shoulder to shoulder.
        int[] across = { 0, -12, 12, -24, 24 };

        for (int dy = 0; dy >= -160; dy -= 8)
        {
            foreach (int dx in across)
            {
                var at = new Vector2(
                    self.X + (mobile.RealScreenPosition.X - player.RealScreenPosition.X) + dx,
                    self.Y + (mobile.RealScreenPosition.Y - player.RealScreenPosition.Y) + dy
                );

                Send(new InputEventMouseMotion
                {
                    Position = at,
                    ButtonMask = holding ? MouseButtonMask.Left : 0
                });

                // Four, and this is not padding: what is under the cursor is
                // worked out while the world is drawn, so a shorter wait reads
                // the answer to the previous question. At two frames this
                // missed a mobile standing one tile away and reported it as
                // not on screen.
                await Frames(host, 4);

                if (!ReferenceEquals(Game.SelectedObject.Object, mobile))
                {
                    continue;
                }

                // Asked twice. Sprites that overlap swap places from frame to
                // frame as they animate, so a pixel that answers with the
                // right mobile once can answer with somebody else by the time
                // the button comes up -- which is how an item offered to
                // another player was dropped on the character offering it.
                await Frames(host, 8);

                Send(new InputEventMouseMotion
                {
                    Position = at,
                    ButtonMask = holding ? MouseButtonMask.Left : 0
                });

                await Frames(host, 4);

                if (ReferenceEquals(Game.SelectedObject.Object, mobile))
                {
                    return at;
                }
            }
        }

        return null;
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

    /// <summary>
    /// Say into the chat bar: opens it first. Bare keystrokes otherwise go
    /// to the hotkeys and the closing enter only opens an empty bar, so a
    /// lone Say never sends (layer shots went nowhere on it).
    /// </summary>
    public static async System.Threading.Tasks.Task SayChat(Node host, string what)
    {
        Send(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        await Frames(host, 2);
        Send(new InputEventKey { Keycode = Key.Enter, Pressed = false });
        await Frames(host, 4);
        await Say(host, what);
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
    private static Vector2 GrabPoint(Game.UI.Controls.Control item)
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
    /// <summary>The first control of type T anywhere under the open gumps, children included.</summary>
    private static T FindControl<T>() where T : Game.UI.Controls.Control
    {
        foreach (Game.UI.Gumps.Gump gump in Game.Managers.UIManager.Gumps)
        {
            T found = FindControl<T>(gump);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static T FindControl<T>(Game.UI.Controls.Control parent) where T : Game.UI.Controls.Control
    {
        if (parent.IsDisposed || !parent.IsVisible)
        {
            return null;
        }

        if (parent is T hit)
        {
            return hit;
        }

        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            T found = FindControl<T>(child);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// ClickGumpButton for a gump that lives inside another one, as the
    /// creation steps do. The button is found by id wherever it is nested and
    /// clicked with the mouse. When <paramref name="leaves"/> is set, the step
    /// should go away; if it does not, the mouse click did not act (seen on
    /// the creation screen's Next arrow and Finish), so the gump's own handler
    /// is called with the same id and that is logged. For a button that only
    /// selects (a city), the handler is always called, since nothing visible
    /// tells whether the click took.
    /// </summary>
    /// <summary>Which login/creation gumps are open right now, for failure lines.</summary>
    private static string OpenLoginGumps()
    {
        var open = new System.Collections.Generic.List<string>();
        void Probe<T>(string n) where T : Game.UI.Controls.Control
        {
            try
            {
                if (Game.Managers.UIManager.GetGump<T>() != null)
                {
                    open.Add(n);
                }
            }
            catch (System.Exception)
            {
            }
        }

        Probe<Game.UI.Gumps.Login.LoginGump>("login");
        Probe<Game.UI.Gumps.Login.CharacterSelectionGump>("charsel");
        Probe<Game.UI.Gumps.CharCreation.CreateCharAppearanceGump>("appearance");
        Probe<Game.UI.Gumps.CharCreation.CreateCharProfessionGump>("profession");
        Probe<Game.UI.Gumps.CharCreation.CreateCharSelectionCityGump>("city");
        return open.Count == 0 ? "none of login/charsel/appearance/profession/city" : string.Join(",", open);
    }

    private static async System.Threading.Tasks.Task ClickChildButton<T>(Node host, int buttonId, string what, bool leaves)
        where T : Game.UI.Controls.Control
    {
        T gump = null;

        for (int i = 0; i < 120 && gump == null; i++)
        {
            gump = FindControl<T>();

            if (gump == null)
            {
                await Frames(host, 1);
            }
        }

        if (gump == null)
        {
            GD.Print($"[GUO] input probe: no {typeof(T).Name} for {what}");

            return;
        }

        Game.UI.Controls.Button button = FindButtonDeep(gump, buttonId);

        if (button != null)
        {
            await Click(host, new Vector2(button.ScreenCoordinateX + button.Width / 2f, button.ScreenCoordinateY + button.Height / 2f));
            await Frames(host, 60);
        }

        if (!leaves || (!gump.IsDisposed && gump.IsVisible && FindControl<T>() == gump))
        {
            if (leaves)
            {
                GD.Print($"[GUO] input probe: the mouse click on {what} did not act; calling {typeof(T).Name}.OnButtonClick({buttonId})");
            }

            gump.OnButtonClick(buttonId);
            await Frames(host, 90);
        }
    }

    /// <summary>FindButton, searching nested controls too.</summary>
    private static Game.UI.Controls.Button FindButtonDeep(Game.UI.Controls.Control parent, int buttonId)
    {
        Game.UI.Controls.Button direct = FindButton(parent, buttonId);

        if (direct != null)
        {
            return direct;
        }

        foreach (Game.UI.Controls.Control child in parent.Children)
        {
            if (child.IsVisible && !(child is Game.UI.Controls.Button))
            {
                Game.UI.Controls.Button found = FindButtonDeep(child, buttonId);

                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Pick a profession with a fixed skill set, not Advanced (which asks for
    /// skills) and not a category (which opens a sub-list).
    /// </summary>
    private static async System.Threading.Tasks.Task ClickPlainProfession(Node host)
    {
        var field = typeof(Game.UI.Gumps.CharCreation.ProfessionInfoGump).GetField(
            "_info", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        Game.UI.Gumps.CharCreation.ProfessionInfoGump pick = null;

        for (int i = 0; i < 120 && pick == null; i++)
        {
            var profession = FindControl<Game.UI.Gumps.CharCreation.CreateCharProfessionGump>();

            if (profession != null)
            {
                foreach (Game.UI.Controls.Control child in profession.Children)
                {
                    if (child is Game.UI.Gumps.CharCreation.ProfessionInfoGump info
                        && field?.GetValue(info) is Assets.ProfessionInfo p
                        && p.Type == Assets.ProfessionLoader.PROF_TYPE.PROFESSION
                        && !string.Equals(p.TrueName, "advanced", System.StringComparison.OrdinalIgnoreCase))
                    {
                        pick = info;
                        GD.Print($"[GUO] input probe: profession {p.TrueName}");

                        break;
                    }
                }
            }

            if (pick == null)
            {
                await Frames(host, 1);
            }
        }

        if (pick == null)
        {
            GD.Print("[GUO] input probe: no plain profession to pick");

            return;
        }

        await Click(host, new Vector2(pick.ScreenCoordinateX + pick.Width / 2f, pick.ScreenCoordinateY + 17));
        await Frames(host, 90);

        // As for the Next arrow (ClickChildButton): if the click did not act,
        // call the entry's own selection handler.
        if (!pick.IsDisposed && FindControl<Game.UI.Gumps.CharCreation.CreateCharProfessionGump>() != null
            && field?.GetValue(pick) is Assets.ProfessionInfo chosen)
        {
            GD.Print("[GUO] input probe: the mouse click on the profession did not act; calling its Selected handler");
            pick.Selected?.Invoke(chosen);
            await Frames(host, 90);
        }
    }

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

    /// <summary>
    /// All eight screen directions in turn, clockwise from up, each held for
    /// <paramref name="frames"/> frames: a showcase walk that faces the camera
    /// every way and ends near where it began. For ObjectsDump's watch.
    /// </summary>
    internal static async System.Threading.Tasks.Task<bool> WalkEight(Node host, int frames)
    {
        Game.GameObjects.PlayerMobile player = Client.Game.UO.World?.Player;
        if (player == null)
        {
            return false;
        }

        int startX = player.X, startY = player.Y;
        bool moved = false;
        foreach (Vector2 offset in new Vector2[] { new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1) })
        {
            await Pull(host, offset.Normalized(), frames);
            await Frames(host, 12);
            moved |= player.X != startX || player.Y != startY;
        }

        return moved;
    }

    /// <summary>The probe's walk (the four screen diagonals, held), for ObjectsDump's watch.</summary>
    internal static System.Threading.Tasks.Task<bool> WalkAround(Node host) => Walk(host);

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
        foreach (Vector2 offset in Offsets)
        {
            // Held. The scene walks a step at a time for as long as the
            // button is down, so this is how far the character goes.
            await Pull(host, offset, 90);

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
    /// Drag the window smaller and back, and see the world follow it.
    /// </summary>
    /// <remarks>
    /// The first thing a player does with a window is change its size, and
    /// with "always use fullsize game window" on -- which is how this client
    /// is played -- the world is supposed to follow. Upstream does that from
    /// an SDL resize event; here it comes from Godot's viewport, through a
    /// different notification, and the only way to know the wire between them
    /// is connected is to pull it.
    ///
    /// Late in the run, because everything the probe clicks is aimed in
    /// window coordinates and this moves all of them. The size is put back
    /// before anything else is asked.
    /// </remarks>
    /// <summary>
    /// Push the window off every monitor and see the client fetch it back.
    /// </summary>
    /// <remarks>
    /// This one is here because it happened: the client came up somewhere no
    /// screen covered, and there was no way to reach it from inside Windows --
    /// no title bar to drag, and a taskbar icon that does nothing, because the
    /// window is not hidden, it is simply where nobody can see it. Getting it
    /// back took SetWindowPos from another process.
    /// </remarks>
    private static async System.Threading.Tasks.Task LoseTheWindow(Node host)
    {
        Godot.DisplayServer.WindowMode mode = Godot.DisplayServer.WindowGetMode();

        if (mode != Godot.DisplayServer.WindowMode.Windowed)
        {
            Godot.DisplayServer.WindowSetMode(Godot.DisplayServer.WindowMode.Windowed);

            await Frames(host, 30);
        }

        Vector2I was = Godot.DisplayServer.WindowGetPosition();

        // Far enough out that no arrangement of monitors could reach it.
        var nowhere = new Vector2I(-30000, -30000);

        Godot.DisplayServer.WindowSetPosition(nowhere);

        await Frames(host, 30);

        Vector2I lost = Godot.DisplayServer.WindowGetPosition();

        Client.Game.PullWindowOntoAScreen();

        await Frames(host, 30);

        Vector2I back = Godot.DisplayServer.WindowGetPosition();
        Vector2I size = Godot.DisplayServer.WindowGetSize();

        long area = (long)size.X * size.Y;
        long seen = 0;

        for (int screen = 0; screen < Godot.DisplayServer.GetScreenCount(); screen++)
        {
            Rect2I shown = new Rect2I(back, size)
                .Intersection(Godot.DisplayServer.ScreenGetUsableRect(screen));

            seen += (long)System.Math.Max(0, shown.Size.X)
                * System.Math.Max(0, shown.Size.Y);
        }

        Godot.DisplayServer.WindowSetPosition(was);

        if (mode != Godot.DisplayServer.WindowMode.Windowed)
        {
            Godot.DisplayServer.WindowSetMode(mode);
        }

        await Frames(host, 30);

        Check(
            "a window lost off screen comes back",
            seen * 4 >= area,
            $"pushed to {lost}, came back to {back} with {seen} of {area} pixels on a screen"
        );
    }

    private static async System.Threading.Tasks.Task ResizeTheWindow(Node host)
    {
        Game.UI.Gumps.WorldViewportGump viewport =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.WorldViewportGump>();

        Game.Scenes.GameScene scene = Client.Game.GetScene<Game.Scenes.GameScene>();

        if (viewport == null || scene == null
            || Configuration.ProfileManager.CurrentProfile?.GameWindowFullSize != true)
        {
            Check("the world follows the window", false, "no full-size world viewport");

            return;
        }

        Vector2I was = Godot.DisplayServer.WindowGetSize();
        Godot.DisplayServer.WindowMode mode = Godot.DisplayServer.WindowGetMode();

        // Windowed first. A maximised window is the window manager's size and
        // not the client's: asking it to be smaller does nothing at all, and
        // the check then reads a viewport that never had anything to follow
        // as a viewport that refused to. The mode goes back afterwards.
        if (mode != Godot.DisplayServer.WindowMode.Windowed)
        {
            Godot.DisplayServer.WindowSetMode(Godot.DisplayServer.WindowMode.Windowed);

            await Frames(host, 30);
        }

        // Smaller, and not by a little: a world that did not move would still
        // look about right after twenty pixels.
        var smaller = new Vector2I(1280, 720);

        Godot.DisplayServer.WindowSetSize(smaller);

        await Frames(host, 60);

        Vector2I now = Godot.DisplayServer.WindowGetSize();
        int wide = scene.Camera.Bounds.Width;
        int high = scene.Camera.Bounds.Height;

        GD.Print(
            $"[GUO] input probe: window {was.X}x{was.Y} ({mode}) -> {now.X}x{now.Y}, "
            + $"camera {wide}x{high}"
        );

        Godot.DisplayServer.WindowSetSize(was);

        if (mode != Godot.DisplayServer.WindowMode.Windowed)
        {
            Godot.DisplayServer.WindowSetMode(mode);
        }

        await Frames(host, 60);

        // Each size divided by the dpi scale, which is what the viewport
        // itself works in: on a scaled display the camera is deliberately
        // smaller than the window in pixels.
        int small = (int)(now.X / Client.Game.DpiScale);
        int big = (int)(Godot.DisplayServer.WindowGetSize().X / Client.Game.DpiScale);

        // Both ways round. A viewport that shrinks and never grows back is a
        // client a player can only make smaller, which is worse than one that
        // ignores the window entirely.
        Check(
            "the world follows the window",
            System.Math.Abs(wide - small) <= 8
                && System.Math.Abs(scene.Camera.Bounds.Width - big) <= 8,
            $"camera {wide} wide in a {now.X} window, {scene.Camera.Bounds.Width} back in a "
            + $"{Godot.DisplayServer.WindowGetSize().X} one"
        );
    }

    /// <summary>
    /// Hold the right button out from the middle of the window, which is how
    /// this client walks: a step at a time for as long as the button is down.
    /// </summary>
    private static async System.Threading.Tasks.Task Pull(Node host, Vector2 offset, int frames)
    {
        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;
        Vector2 at = new Vector2(bounds.Width / 2f, bounds.Height / 2f) + (offset * 250f);

        Send(new InputEventMouseMotion { Position = at });

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Position = at,
            Pressed = true,
        });

        await Frames(host, frames);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Position = at,
            Pressed = false,
        });
    }

    /// <summary>
    /// Keep playing for a while, and see whether the client is still the same
    /// client at the end of it.
    /// </summary>
    /// <remarks>
    /// Three hundred frames standing still says the renderer can draw what is
    /// in front of it; it says nothing about a session. What goes wrong over a
    /// session goes wrong slowly: a cache that only grows, a texture freed on
    /// one path and not another, a list of things to draw that is rebuilt but
    /// never emptied. All three look like a client that is a little worse
    /// every minute, so this walks the character in a square -- which loads
    /// and drops land, statics and mobiles the whole time -- and compares the
    /// last stretch with the first.
    ///
    /// The numbers are deliberately loose. The machine running this is not
    /// idle, the shard is on it, and a collection lands where it lands; what
    /// this is here to catch is a client at half the speed it was, or one
    /// whose memory has no ceiling, not a noisy percent.
    /// </remarks>
    private static async System.Threading.Tasks.Task Endure(Node host, int seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        Game.GameObjects.PlayerMobile player = Client.Game.UO.World?.Player;

        if (player == null)
        {
            Check("the client endures a session", false, "not in the world");

            return;
        }

        GD.Print($"[GUO] input probe: playing on for {seconds} seconds");

        // After everything else, on purpose: every gump the run opened is
        // still on screen and the character has already been about, so this
        // measures a client that has been played rather than a fresh one.
        await Frames(host, 60);

        long objectsAtStart =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectCount);
        long staticAtStart =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.MemoryStatic);
        long texturesAtStart =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTextureMemUsed);

        int startX = player.X;
        int startY = player.Y;
        int far = 0;

        // One leg of the square, in frames. Long enough to cross tiles and
        // pull new ground in, short enough that a leg is a sample rather than
        // the whole run.
        const int Leg = 120;

        var legs = new System.Collections.Generic.List<double>();
        ulong deadline = Godot.Time.GetTicksUsec() + ((ulong)seconds * 1000000UL);
        double worstOfAll = 0;
        long frames = 0;

        for (int leg = 0; Godot.Time.GetTicksUsec() < deadline; leg++)
        {
            ulong begun = Godot.Time.GetTicksUsec();
            ulong previous = begun;
            double worst = 0;
            int counted = 0;

            System.Threading.Tasks.Task pull = Pull(host, Offsets[leg % Offsets.Length], Leg);

            while (!pull.IsCompleted)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

                ulong now = Godot.Time.GetTicksUsec();
                double frame = (now - previous) / 1000.0;
                previous = now;
                counted++;

                if (frame > worst)
                {
                    worst = frame;
                }
            }

            await pull;

            if (counted > 0)
            {
                legs.Add((previous - begun) / 1000.0 / counted);
                frames += counted;
            }

            if (worst > worstOfAll)
            {
                worstOfAll = worst;
            }

            int away = System.Math.Abs(player.X - startX) + System.Math.Abs(player.Y - startY);

            if (away > far)
            {
                far = away;
            }
        }

        long objectsAtEnd =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectCount);
        long staticAtEnd =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.MemoryStatic);
        long texturesAtEnd =
            (long)Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTextureMemUsed);

        GD.Print(
            $"[GUO] input probe: endured {frames} frames over {legs.Count} legs, "
            + $"objects {objectsAtStart} -> {objectsAtEnd}, "
            + $"static {staticAtStart / 1048576.0:F1} -> {staticAtEnd / 1048576.0:F1} MB, "
            + $"textures {texturesAtStart / 1048576.0:F1} -> {texturesAtEnd / 1048576.0:F1} MB, "
            + $"worst frame {worstOfAll:F2} ms, {far} tiles from the start"
        );

        if (legs.Count < 4)
        {
            Check("the client endures a session", false, $"only {legs.Count} legs walked");

            return;
        }

        // A quarter at each end, so one slow leg is not the verdict.
        int span = System.Math.Max(1, legs.Count / 4);
        double first = 0;
        double last = 0;

        for (int i = 0; i < span; i++)
        {
            first += legs[i];
            last += legs[legs.Count - 1 - i];
        }

        first /= span;
        last /= span;

        Check(
            "the client does not slow down as it plays",
            last <= first * 1.25,
            $"{first:F2} ms a frame at the start, {last:F2} ms at the end"
        );

        // Not "no growth": a client walking into a part of the map it has not
        // seen decodes art it did not have, and that art is meant to stay.
        // What is not meant to happen is the count climbing with the clock
        // rather than with the ground covered.
        Check(
            "the client does not run away with memory",
            objectsAtEnd <= objectsAtStart + 4000
                && staticAtEnd <= staticAtStart + (256L * 1048576L),
            $"{objectsAtEnd - objectsAtStart} more objects, "
            + $"{(staticAtEnd - staticAtStart) / 1048576.0:F1} MB more"
        );

        // Still listening. A client that has locked up draws its last frame
        // over and over at a perfectly steady sixty.
        Check(
            "the client still walks at the end",
            far > 0,
            far > 0 ? $"{far} tiles from where it started" : "it never left the spot"
        );
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

    /// <summary>
    /// A click aimed at the world (viewport coordinates). Gump clicks go
    /// through <see cref="Click"/> untouched, but world picking reads
    /// <c>Mouse.Position</c>, which divides the event by <c>DpiScale</c> -- so
    /// a viewport point has to be multiplied back up or it lands short on a
    /// scaled display (and the click misses by a growing margin).
    /// </summary>
    private static async System.Threading.Tasks.Task ClickWorld(Node host, Vector2 at)
    {
        Vector2 scaled = at * Client.Game.DpiScale;
        // Belt and suspenders: the event for the GUI, and the game's mouse
        // state direct (synthetic motion does not always move it -- what the
        // picking reads stayed on the user's real cursor in the splat runs).
        Input.Mouse.Position = new GUO.Compat.Point((int)at.X, (int)at.Y);
        Send(new InputEventMouseMotion { Position = scaled });
        await Frames(host, 2);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = scaled,
            Pressed = true,
        });

        await Frames(host, 6);

        Send(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Position = scaled,
            Pressed = false,
        });

        await Frames(host, 2);
    }

    private static async System.Threading.Tasks.Task DoubleClickWorld(Node host, Vector2 at)
    {
        Vector2 scaled = at * Client.Game.DpiScale;
        Input.Mouse.Position = new GUO.Compat.Point((int)at.X, (int)at.Y);
        Send(new InputEventMouseMotion { Position = scaled });
        await Frames(host, 2);

        for (int i = 0; i < 2; i++)
        {
            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = scaled,
                Pressed = true,
            });

            await Frames(host, 4);

            Send(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = scaled,
                Pressed = false,
            });

            await Frames(host, 4);
        }

        await Frames(host, 40);
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
            + $" in {Game.Managers.UIManager.MouseOverControl?.RootParent?.GetType().Name ?? "none"}"
        );
    }

    /// <summary>
    /// Client pixels to window pixels for the pointer events the probe sends.
    /// One on the desktop; on a device the client draws at a whole-number
    /// scale and divides every pointer position by it (Mouse.Update), so a
    /// probe aiming in client pixels has to multiply first. The dual-screen
    /// probe sets it to the client's DpiScale for the login and resets it.
    /// </summary>
    public static float PointerScale { get; set; } = 1f;

    private static void Send(InputEvent e)
    {
        if (e is InputEventMouse aimed)
        {
            // The probe aims at upstream's 640x480 login layout, at 0,0. A
            // touch build centres that layout (MobileProfile), so aim where
            // the topmost login gump actually is; on a desktop it is 0,0.
            Compat.Point login = LoginOrigin();
            aimed.Position += new Vector2(login.X, login.Y);
        }

        if (PointerScale != 1f && e is InputEventMouse mouse)
        {
            mouse.Position *= PointerScale;
        }

        // Straight into the same queue a real device feeds, so nothing on the
        // path from the window to the client is bypassed.
        Godot.Input.ParseInputEvent(e);
    }

    /// <summary>Where the topmost login-screen gump sits; 0,0 outside the login scene.</summary>
    internal static Compat.Point LoginOrigin()
    {
        if (Client.Game?.Scene is not Game.Scenes.LoginScene)
        {
            return Compat.Point.Zero;
        }

        foreach (Game.UI.Gumps.Gump g in Game.Managers.UIManager.Gumps)
        {
            if (!g.IsDisposed && g.IsVisible && g.GetType().Namespace == "GUO.Game.UI.Gumps.Login")
            {
                return new Compat.Point(g.X, g.Y);
            }
        }

        return Compat.Point.Zero;
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
