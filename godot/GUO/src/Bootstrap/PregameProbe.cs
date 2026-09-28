// SPDX-License-Identifier: BSD-2-Clause

using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps.Login;
using GUO.Input.Touch.Pregame;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// The pre-game card (--pregame-probe), at the login screen: on the second
/// screen where there is one (a device's, or --dual-screen WxH on a
/// desktop), else opened over the login screen from its Servers button.
/// Servers: the dev favourite, adding a server, a favourite, Play and a
/// double tap setting the address, the verdicts, Recent; the community
/// catalogue (a file of the probe's own) with each row's status dot from a
/// real TCP connect to a local listener, Refresh, and the limit of eight
/// timings at once; a shard with its own client files (a fake folder the
/// probe writes, never real shard data): its folder picked through the
/// first-run screen, the restart question, the session file, the resolver
/// taking the folder, and the way back, with the restart held back. Settings: the tabs
/// and groups answer taps, a setting reaches settings.json and the login
/// gump's own box, every group fits the card. Each is photographed. Logs no
/// one in; the servers are kept in a file of the probe's own and the
/// address is put back.
/// </summary>
internal static class PregameProbe
{
    public static bool Passed { get; private set; }

    private static int _failed, _checks;
    private static string _dir, _tag;

    private static void Check(string what, bool ok, string detail = "")
    {
        _checks++;
        GD.Print($"[GUO] pregame probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    /// <param name="world">Also log in (the probe account) and play from the world: the log-out question.</param>
    public static async System.Threading.Tasks.Task Run(Node host, string dir, string tag, bool world = false)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        _tag = string.IsNullOrWhiteSpace(tag) ? "pregame" : tag;
        DirAccess.MakeDirRecursiveAbsolute(_dir);

        PregameCard card = null;

        for (int i = 0; i < 1800 && (card = PregameCard.Instance) == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        _second = DualScreen.HasSecondaryDisplay;

        // One screen with room beside the login gump: the card is docked in
        // the one-screen panel, which counts as the second screen here, and
        // is photographed in the main window.
        for (int i = 0; i < 60 && !_second && DualScreen.IsPanel && !PregameCard.ShownOnSecond; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        _panel = !_second && DualScreen.IsPanel && PregameCard.ShownOnSecond;
        _second |= _panel;

        if (card == null)
        {
            Check("the pre-game card is built at the login screen", false, "not built");
            Finish();
            return;
        }

        await InputProbe.Wait(host, 10);

        if (_second)
        {
            Check("the pre-game card is up on the second screen at the login screen, and no Servers button stands by the login gump",
                PregameCard.ShownOnSecond && UIManager.GetGump<LoginGump>() != null && !PregameCard.ServersButtonShown, card.Geometry);

            if (_panel)
            {
                LoginGump login = UIManager.GetGump<LoginGump>();
                Godot.Rect2I dock = DualScreen.PanelRect;
                Check("one screen: the card is docked on the left and the login gump sits whole to the right of it",
                    DualScreen.PanelShape == "dock" && dock.Position.X == 0 && login != null && login.X >= dock.End.X
                        && login.X + 640 <= Client.Game.ClientBounds.Width,
                    $"dock {dock}, login at {login?.X},{login?.Y}, client {Client.Game.ClientBounds.Width}x{Client.Game.ClientBounds.Height}");
                await SaveMain(host, "login_dock");
            }
        }
        else
        {
            // One screen: the Servers button beside the login gump opens the card, Close shuts it.
            Vector2? at = PregameCard.ServersButtonCentre;
            bool button = at != null;
            await SaveMain(host, "login_button");

            if (at != null)
            {
                Click(at.Value);
                await InputProbe.Wait(host, 10);
            }

            bool opened = PregameCard.OnMain;
            await InputProbe.Wait(host, 5);
            Check("one screen: a Servers button stands beside the login gump and opens the card over it",
                button && opened && !PregameCard.ServersButtonShown, $"button {button}, open {opened}, card {card.Geometry}");
        }

        Configuration.Settings gs = Configuration.Settings.GlobalSettings;
        string ipWas = gs.IP;
        ushort portWas = gs.Port;

        try
        {
            await ServersChecks(host, card);
            await CatalogueChecks(host, card);
            await ShardFilesChecks(host, card);
            await AccountsChecks(host, card);
        }
        finally
        {
            gs.IP = ipWas;
            gs.Port = portWas;
            gs.Save();
            ServerBook.PathOverride = null;
            ServerBook.Load();
            ServerCatalogue.PathOverride = null;
            ServerCatalogue.Load();
            ServerPing.Clear();
            card.Servers.Rebuild();
        }

        // The tabs.
        card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
        await InputProbe.Wait(host, 5);
        bool servers = card.Current == PregameCard.Tab.Servers;
        card.Tap(card.TabButtonFor(PregameCard.Tab.Settings));
        await InputProbe.Wait(host, 5);
        Check("a tap on a tab opens it: Servers, then Settings", servers && card.Current == PregameCard.Tab.Settings);

        PregameSettings settings = card.Settings;
        Vector2 cardSize = card.Size;

        // Every group: a tap opens it, it says something, and it fits the card.
        string misfits = "";

        foreach (string group in PregameSettings.Groups)
        {
            card.Tap(settings.GroupButton(group));
            await InputProbe.Wait(host, 6);

            if (settings.Group != group)
            {
                misfits += $" {group}: not opened;";
                continue;
            }

            if (card.Overflows)
            {
                misfits += $" {group}: the card is taller than the screen ({card.CardSize});";
            }

            foreach (Control c in settings.Fields)
            {
                Rect2 r = c.GetGlobalRect();

                if (c.IsVisibleInTree() && (r.End.X > cardSize.X + 0.5f || r.Position.X < -0.5f))
                {
                    misfits += $" {group}: \"{(c as Label)?.Text ?? (c as Button)?.Text ?? c.GetType().Name}\" {r.Position.X:F0}..{r.End.X:F0};";
                }
            }

            await Save(host, "settings_" + group.ToLowerInvariant().Replace(' ', '_'));
        }

        Check("every settings group opens on a tap and fits the card's width", misfits.Length == 0, misfits.Length == 0 ? $"{PregameSettings.Groups.Length} groups, card {card.Geometry}" : misfits);

        // Profile-only settings are named, not shown disabled.
        card.Tap(settings.GroupButton("Controls"));
        await InputProbe.Wait(host, 5);
        string controls = settings.Text;
        Check("profile-only settings say where they are set",
            controls.Contains("Set in Options once you're in the world") && !settings.Fields.OfType<BaseButton>().Any(b => b.Disabled),
            controls.Length > 120 ? controls[..120] : controls);

        // A setting the login gump also has: through its box, into settings.json.
        card.Tap(settings.GroupButton("Sound"));
        await InputProbe.Wait(host, 5);
        bool musicWas = Settings.GlobalSettings.LoginMusic;
        CheckBox music = settings.Fields.OfType<CheckBox>().FirstOrDefault(b => b.Text == "Login music");
        bool tapped = false, followed = false;

        if (music != null)
        {
            card.Tap(music);
            await InputProbe.Wait(host, 5);
            tapped = Settings.GlobalSettings.LoginMusic != musicWas;
            followed = LoginBox("Music") == Settings.GlobalSettings.LoginMusic;
            card.Tap(music);
            await InputProbe.Wait(host, 5);
        }

        Check("login music: a tap changes the setting and the login gump's own box follows; a second tap puts it back",
            tapped && followed && Settings.GlobalSettings.LoginMusic == musicWas,
            $"box found {music != null}, changed {tapped}, login box followed {followed}, back {Settings.GlobalSettings.LoginMusic == musicWas}");

        // The second screen's own settings.
        card.Tap(settings.GroupButton("Second screen"));
        await InputProbe.Wait(host, 5);
        bool journalWas = DualScreenSettings.Current.Journal;
        CheckBox journal = settings.Fields.OfType<CheckBox>().FirstOrDefault(b => b.Text == "Journal");
        bool changed = false;

        if (journal != null)
        {
            card.Tap(journal);
            await InputProbe.Wait(host, 5);
            changed = DualScreenSettings.Current.Journal != journalWas;
            card.Tap(journal);
            await InputProbe.Wait(host, 5);
        }

        Check("second screen: a tap on Journal changes the shelf setting, and back",
            changed && DualScreenSettings.Current.Journal == journalWas, $"box found {journal != null}, changed {changed}");

        // Leave it on Servers, as a player first sees it.
        card.Tap(settings.GroupButton(PregameSettings.Groups[0]));
        await InputProbe.Wait(host, 5);
        card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
        await InputProbe.Wait(host, 5);

        if (!_second)
        {
            Button close = card.TabButtonFor(PregameCard.Tab.Servers).GetParent().GetChildren().OfType<Button>().FirstOrDefault(b => b.Text == "Close");

            if (close != null)
            {
                card.Tap(close);
            }

            await InputProbe.Wait(host, 10);
            Check("one screen: Close puts the login gump back, with its Servers button", !PregameCard.OnMain && PregameCard.ServersButtonShown);
        }

        if (world && _second && !_panel)
        {
            await WorldChecks(host, card);
        }

        Finish();
    }

    /// <summary>
    /// In the world with the shelf off, the card is the second screen: Play
    /// asks before logging out, Stay keeps the player in, and "Log out and
    /// play" takes them to the login screen.
    /// </summary>
    private static async System.Threading.Tasks.Task WorldChecks(Node host, PregameCard card)
    {
        bool touch = Input.Touch.TouchInput.Enabled;
        Input.Touch.TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;
        await InputProbe.EnterTheWorld(host, 0);
        InputProbe.PointerScale = 1f;
        Input.Touch.TouchInput.Enabled = touch;

        Game.World w = Client.Game.UO.World;

        if (!w.InGame)
        {
            Check("in the world for the log-out question", false, "never got into the world -- is the dev shard running?");
            return;
        }

        bool shelfWas = DualScreenSettings.Current.Enabled;
        DualScreenSettings.Edit(v => v.Enabled = false);

        try
        {
            for (int i = 0; i < 120 && !PregameCard.ShownOnSecond; i++)
            {
                await InputProbe.Wait(host, 1);
            }

            await InputProbe.Wait(host, 20);
            card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
            await InputProbe.Wait(host, 5);

            Configuration.Settings gs = Configuration.Settings.GlobalSettings;
            PregameServers servers = card.Servers;
            ServerEntry here = servers.Listed.FirstOrDefault(e => e.Same(gs.IP, gs.Port));

            if (here == null)
            {
                Check("in the world, the card lists the server in use", false, string.Join(", ", servers.Listed.Select(e => e.Name)));
                return;
            }

            card.Tap(servers.RowFor(here));
            await InputProbe.Wait(host, 4);
            card.Tap(servers.PlayButton);
            await InputProbe.Wait(host, 5);
            bool asked = servers.ConfirmButton != null && servers.ConfirmButton.IsVisibleInTree() && w.InGame;
            await SaveShot(host, "servers_logout");

            Button stay = servers.ConfirmButton?.GetParent().GetChildren().OfType<Button>().FirstOrDefault(b => b.Text == "Stay");

            if (stay != null)
            {
                card.Tap(stay);
            }

            await InputProbe.Wait(host, 10);
            bool stayed = w.InGame && servers.PlayButton != null;

            card.Tap(servers.PlayButton);
            await InputProbe.Wait(host, 5);

            if (servers.ConfirmButton != null)
            {
                card.Tap(servers.ConfirmButton);
            }

            for (int i = 0; i < 300 && Client.Game.Scene is not Game.Scenes.LoginScene; i++)
            {
                await InputProbe.Wait(host, 1);
            }

            bool out_ = Client.Game.Scene is Game.Scenes.LoginScene;
            asked &= !_overflow.Contains("servers_logout");
            Check("in the world, Play asks \"Log out and play\"; Stay keeps the player in; yes logs out to the login screen",
                asked && stayed && out_, $"asked {asked}, stayed {stayed}, at login {out_}, status \"{servers.Status}\"");

            if (out_)
            {
                for (int i = 0; i < 120 && UIManager.GetGump<LoginGump>() == null; i++)
                {
                    await InputProbe.Wait(host, 1);
                }

                await InputProbe.Wait(host, 10);
                await AccountLoginChecks(host, card);
            }
        }
        finally
        {
            DualScreenSettings.Edit(v => v.Enabled = shelfWas);
        }
    }

    private static bool? LoginBox(string text)
    {
        LoginGump login = UIManager.GetGump<LoginGump>();

        if (login == null)
        {
            return null;
        }

        return All(login).OfType<Game.UI.Controls.Checkbox>().FirstOrDefault(c => c.Text == text)?.IsChecked;
    }

    private static System.Collections.Generic.IEnumerable<Game.UI.Controls.Control> All(Game.UI.Controls.Control c) =>
        c.Children.SelectMany(x => new[] { x }.Concat(All(x)));

    private static void Finish()
    {
        Passed = _failed == 0;
        GD.Print($"[GUO] pregame probe: {_checks - _failed}/{_checks} checks passed");
        GD.Print(Passed ? "[GUO] pregame: ok" : "[GUO] pregame: FAIL");
    }

    private static bool _second, _panel;

    /// <summary>A left click in window pixels, through the card's main-window input as the mouse or a finger would.</summary>
    private static void Click(Vector2 at)
    {
        if (Input.Touch.TouchInput.Enabled)
        {
            PregameCard.HandleMainInput(new InputEventScreenTouch { Position = at, Pressed = true });
            PregameCard.HandleMainInput(new InputEventScreenTouch { Position = at, Pressed = false });
        }
        else
        {
            PregameCard.HandleMainInput(new InputEventMouseButton { Position = at, ButtonIndex = MouseButton.Left, Pressed = true });
            PregameCard.HandleMainInput(new InputEventMouseButton { Position = at, ButtonIndex = MouseButton.Left, Pressed = false });
        }
    }

    private static async System.Threading.Tasks.Task ServersChecks(Node host, PregameCard card)
    {
        // A servers.json of the probe's own, empty.
        ServerBook.PathOverride = ProjectSettings.GlobalizePath($"user://probe_servers_{_tag}.json");
        System.IO.File.Delete(ServerBook.PathOverride);
        ServerBook.Load();

        card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
        await InputProbe.Wait(host, 6);
        PregameServers servers = card.Servers;
        Configuration.Settings gs = Configuration.Settings.GlobalSettings;

        // A dev build lists its shard, and says where it came from, not its address.
        ServerEntry dev = servers.Listed.FirstOrDefault(e => e.Dev);
        bool debug = OS.IsDebugBuild();

        if (dev != null)
        {
            card.Tap(servers.RowFor(dev));
            await InputProbe.Wait(host, 4);
        }

        Check("a dev build lists its dev shard as a favourite, without showing its address",
            debug ? dev != null && dev.Favourite && !servers.DetailText.Contains(dev.Host) : dev == null,
            $"debug {debug}, dev listed {dev != null}, page \"{Cut(servers.DetailText)}\"");

        // Add a server by hand: the form, three fields, Save.
        card.Tap(servers.AddButton);
        await InputProbe.Wait(host, 5);
        LineEdit[] f = servers.AddFields;
        bool saved = false;

        if (servers.Adding && f[0] != null)
        {
            await SaveShot(host, "servers_add");
            await Reveal(host, card, f[0]);
            card.Type("Probe shard");
            await Reveal(host, card, f[1]);
            card.Type("probe.invalid");
            await Reveal(host, card, f[2]);
            f[2].Text = "";
            card.Type("2594");
            await Reveal(host, card, servers.SaveButton);
            await InputProbe.Wait(host, 6);
            saved = !servers.Adding && servers.Selected?.Name == "Probe shard" && servers.Selected.Port == 2594;
        }

        ServerEntry own = servers.Selected;
        Check("Add server: typed name, address and port are saved, listed under Your servers and chosen; its page shows the address",
            saved && own.Own && servers.DetailText.Contains("probe.invalid, port 2594"),
            $"form {servers.Adding}, saved {saved}, page \"{Cut(servers.DetailText)}\"");

        if (!saved)
        {
            return;
        }

        // A favourite, and back.
        card.Tap(servers.FavouriteButton);
        await InputProbe.Wait(host, 5);
        bool fav = own.Favourite && ServerBook.Favourites.Contains(own);
        card.Tap(servers.FavouriteButton);
        await InputProbe.Wait(host, 5);
        Check("Favourite moves it to Favourites; Unfavourite back", fav && !own.Favourite && ServerBook.Own.Contains(own));

        // Play at the login step with no account typed: the address swaps, nothing connects.
        card.Tap(servers.RowFor(own));
        await InputProbe.Wait(host, 3);
        await SaveShot(host, "servers");
        card.Tap(servers.PlayButton);
        await InputProbe.Wait(host, 6);
        Game.Scenes.LoginScene login = Client.Game.GetScene<Game.Scenes.LoginScene>();
        Check("Play (the login arrow) sets the login gump's server; with no account typed it asks for one and connects nowhere",
            gs.IP == "probe.invalid" && gs.Port == 2594 && login?.CurrentLoginStep == Game.Scenes.LoginSteps.Main && servers.Status.Contains("Type your account"),
            $"address {(gs.IP == "probe.invalid" ? "swapped" : "not swapped")}, step {login?.CurrentLoginStep}, status \"{servers.Status}\"");

        // A double tap on a row plays it (the dev shard, in a dev build).
        if (dev != null)
        {
            card.Tap(servers.RowFor(dev));
            await InputProbe.Wait(host, 2);
            card.Tap(servers.RowFor(dev));
            await InputProbe.Wait(host, 6);
            Check("a double tap on a row plays it", dev.Same(gs.IP, gs.Port), $"address on the dev shard {dev.Same(gs.IP, gs.Port)}");
        }

        // Verdicts: a shard that allows only its own client, one that needs other files.
        var closed = new ServerEntry { Name = "Closed", Host = "a.invalid", ThirdPartyClients = false };
        var other = new ServerEntry { Name = "Other", Host = "b.invalid", ClientVersion = "5.0.9.1" };
        var fine = new ServerEntry { Name = "Fine", Host = "c.invalid", ClientVersion = gs.ClientVersion };
        ServerPlay.Verdict vc = ServerPlay.Check(closed, out string rc);
        ServerPlay.Verdict vo = ServerPlay.Check(other, out string ro);
        ServerPlay.Verdict vf = ServerPlay.Check(fine, out _);
        Check("a shard for its own client only, and one that needs other client files, say why and do not play",
            vc == ServerPlay.Verdict.NotAllowed && vo == ServerPlay.Verdict.NeedsOwnData && vf == ServerPlay.Verdict.Ready
            && rc == "This shard only allows its own client." && ro.Contains("needs its own client files"),
            $"{vc}: \"{rc}\"; {vo}: \"{ro}\"; same version {vf}");

        // Recent: a server logged in to is kept, newest first, five at most.
        string ipNow = gs.IP;
        ushort portNow = gs.Port;
        string nameNow = gs.LastServerName;

        for (int i = 0; i < 7; i++)
        {
            gs.IP = $"recent{i}.invalid";
            gs.Port = 2593;
            gs.LastServerName = $"Shard {(char) ('A' + i)}";
            ServerBook.NoteWorld(true);
            ServerBook.NoteWorld(false);
        }

        gs.IP = ipNow;
        gs.Port = portNow;
        gs.LastServerName = nameNow;
        var recent = ServerBook.Recent.ToList();
        Check("Recent keeps the last five servers logged in to, newest first",
            recent.Count == ServerBook.RecentKept && recent[0].Host == "recent6.invalid" && recent[^1].Host == "recent2.invalid",
            string.Join(", ", recent.Select(e => e.Host)));

        card.Servers.Rebuild();
        await InputProbe.Wait(host, 5);
        await SaveShot(host, "servers_recent");

        Rect2 play = servers.PlayButton?.GetGlobalRect() ?? default;
        Check("the servers page fits its card: it never grows past the screen, and Play is in view",
            _overflow.Length == 0 && servers.PlayButton != null && play.End.Y <= card.Size.Y && play.End.X <= card.Size.X,
            $"overflowing in:{(_overflow.Length == 0 ? " none" : _overflow)}; Play at {play.Position.X:F0},{play.Position.Y:F0} on a {card.Size.X}x{card.Size.Y} card");

        // The probe's own server goes.
        ServerBook.Remove(own);
    }

    private static async System.Threading.Tasks.Task CatalogueChecks(Node host, PregameCard card)
    {
        // Two local ports: one listening (a shard that answers), one just shut (one that doesn't).
        var open = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        open.Start();
        var shutter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        shutter.Start();
        int openPort = ((System.Net.IPEndPoint) open.LocalEndpoint).Port;
        int shutPort = ((System.Net.IPEndPoint) shutter.LocalEndpoint).Port;
        shutter.Stop();

        try
        {
            string path = ProjectSettings.GlobalizePath($"user://probe_catalogue_{_tag}.json");
            ServerCatalogue.PathOverride = path;
            System.IO.File.WriteAllText(path, "{ \"version\": 1, \"servers\": ["
                + $"{{ \"name\": \"Probe Answers\", \"host\": \"127.0.0.1\", \"port\": {openPort}, \"era\": \"AOS\", \"emulator\": \"ModernUO\", \"third_party_clients\": true, \"site\": \"https://example.com\", \"description\": \"A catalogue shard the probe listens for.\" }},"
                + $"{{ \"name\": \"Probe Silent\", \"host\": \"127.0.0.1\", \"port\": {shutPort}, \"era\": \"T2A\", \"third_party_clients\": true }},"
                + "{ \"name\": \"Probe Other Client\", \"host\": \"c.invalid\", \"port\": 2593, \"client_version\": \"5.0.9.1\", \"third_party_clients\": true },"
                + "{ \"name\": \"Probe Own Client Only\", \"host\": \"d.invalid\", \"port\": 2593, \"third_party_clients\": false },"
                + "{ \"name\": \"Probe No Host\", \"port\": 2593 }"
                + "] }");
            ServerPing.Clear();

            PregameServers servers = card.Servers;
            card.Tap(servers.RefreshButton);
            await InputProbe.Wait(host, 5);

            ServerEntry answers = servers.Listed.FirstOrDefault(e => e.Name == "Probe Answers");
            ServerEntry silent = servers.Listed.FirstOrDefault(e => e.Name == "Probe Silent");
            ServerEntry other = servers.Listed.FirstOrDefault(e => e.Name == "Probe Other Client");
            Check("Community lists the catalogue's shards, less one for its own client only and one without an address",
                answers != null && silent != null && other != null && ServerCatalogue.Servers.Count == 3 && !servers.Listed.Any(e => e.Name.StartsWith("Probe Own") || e.Name.StartsWith("Probe No")),
                $"catalogue {ServerCatalogue.Servers.Count}: {string.Join(", ", ServerCatalogue.Servers.Select(e => e.Name))}");

            if (answers == null || silent == null || other == null)
            {
                return;
            }

            // The timings land within the 3 s timeout (the shut port refuses at once).
            for (int i = 0; i < 60 && (servers.RowStatus(answers).Dot != ServerPing.Kind.Up || servers.RowStatus(silent).Dot != ServerPing.Kind.Down); i++)
            {
                await InputProbe.Wait(host, 6);
            }

            // Red is for a shard GUO can't play at all; one for another client can, after a restart.
            ServerEntry closed = ServerBook.Add("Probe Closed", "closed.invalid", "2598", out _);
            closed.ThirdPartyClients = false;
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            var a = servers.RowStatus(answers);
            var b = servers.RowStatus(silent);
            var c = servers.RowStatus(other);
            bool closedRed = servers.RowStatus(closed).Red;
            ServerBook.Remove(closed);
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            Check("a row's dot is gold with its time when the shard answers, hollow with a dash when it doesn't, red only when GUO can't play there",
                a.Dot == ServerPing.Kind.Up && !a.Red && a.Ping.EndsWith(" ms") && b.Dot == ServerPing.Kind.Down && !b.Red && b.Ping == "\u2014" && !c.Red && closedRed,
                $"answers {a.Dot}/{a.Ping}, silent {b.Dot}/{b.Ping}, other client red {c.Red}, own client only red {closedRed}");

            await Reveal(host, card, servers.RowFor(answers));
            await InputProbe.Wait(host, 4);
            await SaveShot(host, "servers_community");
            bool answering = servers.DetailText.Contains("Answering,") && servers.SiteButton != null && !servers.DetailText.Contains("127.0.0.1");
            await Reveal(host, card, servers.RowFor(silent));
            await InputProbe.Wait(host, 4);
            await SaveShot(host, "servers_silent");
            Check("a catalogue shard's page: its time and Site when it answers, and without its address; the silent one says why",
                answering && servers.DetailText.Contains("Probe Silent isn't answering (no reply in 3 s). It may be down, or the address is wrong."),
                $"answering page {answering}, silent page \"{Cut(servers.DetailText)}\"");

            // A favourite catalogue shard is the player's: it lists under Favourites once.
            await Reveal(host, card, servers.RowFor(answers));
            await InputProbe.Wait(host, 4);
            card.Tap(servers.FavouriteButton);
            await InputProbe.Wait(host, 5);
            ServerEntry kept = servers.Selected;
            int listedOnce = servers.Listed.Count(e => e.Same("127.0.0.1", openPort));
            bool faved = kept != null && kept.Favourite && ServerBook.Favourites.Contains(kept) && listedOnce == 1;
            card.Tap(servers.FavouriteButton);
            await InputProbe.Wait(host, 5);
            ServerBook.Remove(kept);
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            Check("Favourite on a catalogue shard keeps a copy under Favourites, listed once",
                faved, $"favourite {kept?.Favourite}, listed {listedOnce} time(s)");

            // A catalogue that doesn't read: the group says so, and Refresh reads it again.
            System.IO.File.WriteAllText(path, "{ not json");
            card.Tap(servers.RefreshButton);
            await InputProbe.Wait(host, 5);
            string empty = string.Join(" | ", servers.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text));
            await SaveShot(host, "servers_catalogue_failed");
            Check("a catalogue that can't be read leaves the saved servers and says so",
                !ServerCatalogue.Loaded && empty.Contains("The server list couldn't be loaded. Your saved servers are above. Refresh to try again."),
                $"loaded {ServerCatalogue.Loaded}");

            // Twelve at once: never more than eight connects open.
            ServerPing.Clear();
            var many = Enumerable.Range(0, 12).Select(i => new ServerEntry { Name = $"t{i}", Host = $"127.0.0.{i + 2}", Port = shutPort }).ToList();

            foreach (ServerEntry e in many)
            {
                ServerPing.Want(e);
            }

            for (int i = 0; i < 90 && many.Any(e => ServerPing.Get(e).Busy); i++)
            {
                await InputProbe.Wait(host, 6);
            }

            Check("at most eight timings run at once, and each ends",
                ServerPing.MostAtOnce <= ServerPing.AtOnce && ServerPing.MostAtOnce > 0 && many.All(e => !ServerPing.Get(e).Busy && ServerPing.Get(e).Kind != ServerPing.Kind.Unknown),
                $"most at once {ServerPing.MostAtOnce}, ended {many.Count(e => !ServerPing.Get(e).Busy)} of {many.Count}");
        }
        finally
        {
            open.Stop();
        }
    }

    private static string _clickDetail = "";

    /// <summary>
    /// Accounts, with a fake one made for the run (its password random and
    /// never printed): saved through the page, kept only as the keystore's
    /// ciphertext, read back, useless on another entry, forgotten.
    /// </summary>
    private static async System.Threading.Tasks.Task AccountsChecks(Node host, PregameCard card)
    {
        PregameServers servers = card.Servers;
        ServerEntry e = ServerBook.Add("Probe Accounts", "accounts.invalid", "2595", out _);
        ServerEntry moved = ServerBook.Add("Probe Moved", "moved.invalid", "2596", out _);
        string password = System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        Input.Touch.Pregame.Accounts.ISecretStore store = Input.Touch.Pregame.Accounts.SecretStore.Current;

        try
        {
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            await Reveal(host, card, servers.RowFor(e));
            await InputProbe.Wait(host, 4);
            bool none = servers.AddAccountButton != null && servers.DetailText.Contains("None saved.");
            await Reveal(host, card, servers.AddAccountButton);
            await InputProbe.Wait(host, 4);
            bool form = servers.AccountField != null && servers.PasswordField is { Secret: true } && servers.AccountSaveButton != null;

            if (form)
            {
                servers.AccountField.Text = "guoprobe";
                servers.PasswordField.Text = password;

                if (servers.KeepBox != null)
                {
                    servers.KeepBox.ButtonPressed = true;
                }

                await SaveShot(host, "servers_account_add");
                card.Tap(servers.AccountSaveButton);
                await InputProbe.Wait(host, 4);
            }

            Input.Touch.Pregame.Accounts.SavedAccount a = Input.Touch.Pregame.Accounts.AccountBook.For(e).FirstOrDefault(x => x.Name == "guoprobe");
            string file = System.IO.File.ReadAllText(ServerBook.FilePath);
            bool clean = !file.Contains(password) && !file.Contains(Utility.Crypter.Encrypt(password)) && !file.Contains(System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password)));
            await SaveShot(host, "servers_account_saved");
            Check("Add account on a server's page saves it; servers.json keeps no password (neither plain, nor Crypter, nor base64)",
                none && form && a != null && clean && servers.PickedAccount == a && (store.Available ? a.HasPassword && a.Secret.Store == store.Kind : !a.HasPassword),
                $"empty note {none}, form {form}, saved {a != null}, store {a?.Secret?.Store ?? "none"} ({store.Kind}), file clean {clean}, picked {servers.PickedAccount?.Name}");

            if (store.Available && a != null)
            {
                string back = Input.Touch.Pregame.Accounts.AccountBook.Password(e, a, out string why);
                string stolen = store.Unprotect(Input.Touch.Pregame.Accounts.AccountBook.Binding(moved, a.Name), a.Secret, out string whyMoved);
                Check($"the {store.Kind} store gives the password back, and not for another entry it's copied onto",
                    back == password && stolen == null,
                    $"round trip {(back == password ? "same" : "different: " + why)}, on another entry {(stolen == null ? "refused (" + whyMoved + ")" : "decrypted")}");
            }

            if (a != null)
            {
                await Reveal(host, card, servers.ForgetAccountButton);
                await InputProbe.Wait(host, 4);
                file = System.IO.File.ReadAllText(ServerBook.FilePath);
                Check("Forget removes the account and its secret from servers.json",
                    Input.Touch.Pregame.Accounts.AccountBook.For(e).Count == 0 && !file.Contains("guoprobe") && servers.PickedAccount == null,
                    $"left {Input.Touch.Pregame.Accounts.AccountBook.For(e).Count}, in the file {file.Contains("guoprobe")}");
            }
        }
        finally
        {
            ServerBook.Remove(e);
            ServerBook.Remove(moved);
            servers.Rebuild();
        }
    }

    /// <summary>
    /// At the login screen, on the shard: a login the accounts manager starts
    /// reaches the shard and leaves settings.json's password as it was, with
    /// the classic Save account box ticked; a typed login with the box ticked
    /// is saved the upstream way. The account is the probe's test account.
    /// </summary>
    private static async System.Threading.Tasks.Task AccountLoginChecks(Node host, PregameCard card)
    {
        Configuration.Settings gs = Configuration.Settings.GlobalSettings;
        PregameServers servers = card.Servers;
        ServerEntry here = servers.Listed.FirstOrDefault(x => x.Same(gs.IP, gs.Port));
        var store = Input.Touch.Pregame.Accounts.SecretStore.Current;

        if (here == null || !store.Available)
        {
            Check("a saved account logs in from the Servers tab", false, here == null ? "the server in use isn't listed" : $"no keystore here ({store.Kind})");
            return;
        }

        bool saveWas = gs.SaveAccount;
        string userWas = gs.Username;
        string passWas = gs.Password;
        string mark = Utility.Crypter.Encrypt("guo-probe-mark");
        Game.Scenes.LoginScene Login() => Client.Game.GetScene<Game.Scenes.LoginScene>();

        async System.Threading.Tasks.Task<bool> Reached()
        {
            for (int i = 0; i < 300; i++)
            {
                Game.Scenes.LoginSteps step = Login()?.CurrentLoginStep ?? Game.Scenes.LoginSteps.Main;

                if (step is Game.Scenes.LoginSteps.ServerSelection or Game.Scenes.LoginSteps.CharacterSelection)
                {
                    return true;
                }

                await InputProbe.Wait(host, 1);
            }

            return false;
        }

        // The login gump's own box: its arrow copies the box into the setting.
        void Tick(bool on)
        {
            LoginGump g = UIManager.GetGump<LoginGump>();
            Game.UI.Controls.Checkbox box = g == null ? null : All(g).OfType<Game.UI.Controls.Checkbox>().FirstOrDefault(c => c.Text == Resources.ResGumps.SaveAccount);

            if (box != null)
            {
                box.IsChecked = on;
            }

            gs.SaveAccount = on;
        }

        async System.Threading.Tasks.Task Back()
        {
            Network.NetClient.Socket.Disconnect();
            Client.Game.SetScene(new Game.Scenes.LoginScene(Client.Game.UO.World));

            for (int i = 0; i < 120 && UIManager.GetGump<LoginGump>() == null; i++)
            {
                await InputProbe.Wait(host, 1);
            }

            await InputProbe.Wait(host, 10);
        }

        try
        {
            Tick(true);
            gs.Username = "";
            gs.Password = mark;
            Input.Touch.Pregame.Accounts.AccountBook.Add(here, InputProbe.ProbeAccount, InputProbe.ProbePassword, true, out _);
            card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            await Reveal(host, card, servers.RowFor(servers.Listed.First(x => x.Same(gs.IP, gs.Port))));
            await InputProbe.Wait(host, 4);
            bool picked = servers.PickedAccount?.Name == InputProbe.ProbeAccount;
            bool ticked = LoginBox(Resources.ResGumps.SaveAccount) == true;
            await SaveShot(host, "servers_account_play");
            card.Tap(servers.PlayButton);
            bool reached = await Reached();
            bool kept = gs.Password == mark && gs.Username == "";
            Check("a saved account logs in with Play (the shard answers with its server list), and settings.json keeps no copy of it with Save account ticked",
                picked && ticked && reached && kept && gs.SaveAccount, $"picked {picked}, box ticked {ticked}/{gs.SaveAccount}, reached {reached} ({Login()?.CurrentLoginStep}), settings untouched {kept}, status \"{servers.Status}\"");
            await Back();

            // A typed login, the box ticked: upstream's way, unchanged.
            LoginGump gump = UIManager.GetGump<LoginGump>();
            var boxes = gump == null ? new System.Collections.Generic.List<Game.UI.Controls.StbTextBox>() : All(gump).OfType<Game.UI.Controls.StbTextBox>().ToList();
            bool typed = false;

            if (boxes.Count >= 2)
            {
                Tick(true);
                boxes[0].SetText(InputProbe.ProbeAccount);
                boxes[1].SetText(InputProbe.ProbePassword);
                gump.OnButtonClick(0);
                typed = await Reached();
            }

            bool classic = gs.Username == InputProbe.ProbeAccount && gs.Password == Utility.Crypter.Encrypt(InputProbe.ProbePassword);
            Check("a typed login with the classic Save account box ticked is saved the upstream way",
                typed && classic, $"reached {typed}, saved as upstream {classic} (box {gs.SaveAccount}, name {gs.Username == InputProbe.ProbeAccount}, password {gs.Password == Utility.Crypter.Encrypt(InputProbe.ProbePassword)}, still the mark {gs.Password == mark}, gumps {boxes.Count})");
            await Back();
        }
        finally
        {
            foreach (var a in Input.Touch.Pregame.Accounts.AccountBook.For(here).Where(x => x.Name == InputProbe.ProbeAccount).ToList())
            {
                Input.Touch.Pregame.Accounts.AccountBook.Forget(here, a);
            }

            Tick(saveWas);
            gs.Username = userWas;
            gs.Password = passWas;
            gs.Save();
        }
    }

    private static async System.Threading.Tasks.Task ShardFilesChecks(Node host, PregameCard card)
    {
        // A fake shard folder: a layered guo_data.json over one file of GUO's
        // own making; and one whose manifest is incomplete.
        string good = ProjectSettings.GlobalizePath($"user://probe_shard_files_{_tag}");
        string bad = ProjectSettings.GlobalizePath($"user://probe_shard_bad_{_tag}");
        System.IO.Directory.CreateDirectory(good);
        System.IO.Directory.CreateDirectory(bad);
        System.IO.File.WriteAllText(System.IO.Path.Combine(good, "guo_probe.txt"), "made by the pregame probe");
        System.IO.File.WriteAllText(System.IO.Path.Combine(good, DataSources.Manifest),
            "{ \"format\": \"guo/data-folder@1\", \"mode\": \"layered\", \"contains_ea_data\": false, \"license\": \"CC0\", \"source\": \"GUO pregame probe\", \"files\": { \"guo_probe.txt\": {} } }");
        System.IO.File.WriteAllText(System.IO.Path.Combine(bad, DataSources.Manifest), "{ \"format\": \"guo/data-folder@1\", \"mode\": \"layered\" }");

        string sessionWas = ShardSession.FilePath;
        ShardSession.FilePath = ProjectSettings.GlobalizePath($"user://probe_shard_session_{_tag}.json");
        System.IO.File.Delete(ShardSession.FilePath);
        int restarts = 0;
        ShardSession.RestartHook = () => restarts++;
        Configuration.Settings gs = Configuration.Settings.GlobalSettings;
        PregameServers servers = card.Servers;
        ServerEntry e = ServerBook.Add("Probe Custom", "custom.invalid", "2597", out _);

        try
        {
            e.NeedsCustomData = true;
            ServerBook.Save();
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            await Reveal(host, card, servers.RowFor(e));
            await InputProbe.Wait(host, 4);
            Check("a shard that needs its own files says so and can still be played (Play is lit, its dot not red)",
                servers.DetailText.Contains("needs its own client files. Choose where they are, and GUO restarts with them.")
                && servers.PlayButton != null && !servers.PlayButton.Disabled && !servers.RowStatus(e).Red,
                $"page \"{Cut(servers.DetailText)}\"");

            // Play: the first-run screen, for its folder. A bad pick is refused; the fake one is taken.
            card.Tap(servers.PlayButton);
            await InputProbe.Wait(host, 6);
            FirstRunScreen picker = servers.Picker;
            bool open = picker != null && FirstRunScreen.IsOpen;
            bool refused = false, taken = false;

            if (open)
            {
                picker.Pick(bad);
                await InputProbe.Wait(host, 2);
                refused = picker.ContinueButton.Disabled;
                picker.Pick(good);
                await InputProbe.Wait(host, 3);
                taken = !picker.ContinueButton.Disabled;
                await SaveMain(host, "shard_files_picker");

                // Save with a click pushed into the main window, through the game's input.
                Vector2 at = picker.ContinueButton.GetGlobalRect().GetCenter();
                Viewport main = picker.GetViewport();
                main.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at }, true);
                await InputProbe.Wait(host, 2);
                main.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
                await InputProbe.Wait(host, 2);
                main.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
                await InputProbe.Wait(host, 8);
                _clickDetail = "";

                // The desktop's dual-screen simulator does not let a pushed
                // click reach the main window's controls (one screen does):
                // Save is pressed directly there, and the detail says so.
                if (FirstRunScreen.IsOpen && _second)
                {
                    picker.ContinueButton.EmitSignal(BaseButton.SignalName.Pressed);
                    await InputProbe.Wait(host, 8);
                    _clickDetail = "; the pushed click did not land in the dual-screen simulator, Save pressed directly";
                }
            }

            ServerEntry kept = ServerBook.Find("custom.invalid", 2597);
            string saved = System.IO.File.ReadAllText(ServerBook.FilePath);
            Check("Play opens the first-run screen for its files: a folder without a whole manifest is refused, the fake one saved with a click, kept in servers.json",
                open && refused && taken && !FirstRunScreen.IsOpen && kept?.DataFolder == good && saved.Contains("\"data_folder\""),
                $"picker {open}, bad refused {refused}, good taken {taken}, still open {FirstRunScreen.IsOpen}, kept \"{kept?.DataFolder}\" (picked \"{good}\"), in the file {saved.Contains("\"data_folder\"")}{_clickDetail}");

            // Then the question, and a restart (held back) with the session written down.
            await SaveShot(host, "servers_shard_restart");
            bool asked = servers.DetailText.Contains("Restart GUO with Probe Custom's files?") && servers.ConfirmButton != null;

            if (asked)
            {
                card.Tap(servers.ConfirmButton);
                await InputProbe.Wait(host, 4);
            }

            string json = System.IO.File.Exists(ShardSession.FilePath) ? System.IO.File.ReadAllText(ShardSession.FilePath) : "";
            Check("\"Restart GUO\" writes the session (the shard, its folder, the player's own encryption) and restarts",
                asked && restarts == 1 && json.Contains("\"Probe Custom\"") && json.Contains("2597") && json.Contains($"probe_shard_files_{_tag}") && json.Contains("own_encryption"),
                $"asked {asked}, restarts {restarts}, session {(json.Length > 0 ? "written" : "missing")}");

            // What the next start does with it: the session read, the folder in the custom slot over the install.
            ShardSession.Data d = ShardSession.Load();
            DataSources.Result r = DataSources.Resolve(new DataSources.Inputs { CustomFlag = d?.DataFolder ?? "", InstallConfigured = gs.UltimaOnlineDirectory, InstallConfiguredOrigin = "setting" });
            Check("the next start reads the session and puts its folder in ADR-0021's custom slot over the install",
                ShardSession.Active && ShardSession.FolderKind(good, out _) == "custom" && r.Source == "install+custom" && r.Overrides.ContainsKey("guo_probe.txt"),
                $"active {ShardSession.Active}, resolved {r.Source}, overrides {string.Join(",", r.Overrides.Keys)}");

            // Running with its files: the banner and the way back.
            servers.Rebuild();
            await InputProbe.Wait(host, 3);
            await Reveal(host, card, servers.RowFor(ServerBook.Find("custom.invalid", 2597)));
            await InputProbe.Wait(host, 4);
            bool running = servers.BackButton != null && ServerPlay.Check(kept, out _) == ServerPlay.Verdict.Ready && servers.DetailText.Contains("GUO is running with its files now.");
            await SaveShot(host, "servers_shard_session");
            await Reveal(host, card, servers.BackButton);
            await InputProbe.Wait(host, 4);
            bool askedBack = servers.DetailText.Contains("Restart GUO with your own files?");

            if (askedBack)
            {
                card.Tap(servers.ConfirmButton);
                await InputProbe.Wait(host, 4);
            }

            json = System.IO.File.Exists(ShardSession.FilePath) ? System.IO.File.ReadAllText(ShardSession.FilePath) : "";
            ShardSession.Data back = ShardSession.Load();
            Check("with its files: the list says so, the shard plays as is, and \"Your own files\" restarts back (a one-shot file, gone once read)",
                running && askedBack && restarts == 2 && back != null && back.DataFolder == null && !json.Contains($"probe_shard_files_{_tag}")
                && !ShardSession.Active && !System.IO.File.Exists(ShardSession.FilePath),
                $"banner {servers.BackButton != null}, running {running}, asked {askedBack}, restarts {restarts}");
        }
        finally
        {
            ShardSession.RestartHook = null;
            ShardSession.SetCurrentForProbe(null);
            ShardSession.TryDelete();
            ShardSession.FilePath = sessionWas;
            ServerEntry left = ServerBook.Find("custom.invalid", 2597);

            if (left != null)
            {
                ServerBook.Remove(left);
            }

            servers.Rebuild();
        }
    }

    /// <summary>Scrolls a control into view (a finger would), lets the layout settle, taps it.</summary>
    private static async System.Threading.Tasks.Task Reveal(Node host, PregameCard card, Control c)
    {
        for (Node n = c.GetParent(); n != null; n = n.GetParent())
        {
            if (n is ScrollContainer scroll)
            {
                scroll.EnsureControlVisible(c);
                break;
            }
        }

        await InputProbe.Wait(host, 3);
        card.Tap(c);
    }

    private static string Cut(string s) => s.Length > 140 ? s[..140] : s;

    /// <summary>A photograph of wherever the card is.</summary>
    private static async System.Threading.Tasks.Task SaveShot(Node host, string name)
    {
        await (_second ? Save(host, name) : SaveMain(host, name));

        if (PregameCard.Instance is PregameCard c && c.Overflows)
        {
            _overflow += $" {name}";
        }
    }

    private static string _overflow = "";

    private static async System.Threading.Tasks.Task SaveMain(Node host, string name)
    {
        await InputProbe.Wait(host, 6);
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        string path = _dir.PathJoin($"{_tag}_{name}.png");
        host.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[GUO] pregame probe: {name} -> {ProjectSettings.GlobalizePath(path)}");
    }

    private static async System.Threading.Tasks.Task Save(Node host, string name)
    {
        if (!_second || _panel)
        {
            await SaveMain(host, name);
            return;
        }

        await InputProbe.Wait(host, 6);
        string path = _dir.PathJoin($"{_tag}_{name}_second.png");

        if (DualScreen.SaveFrame(path))
        {
            GD.Print($"[GUO] pregame probe: {name} -> {ProjectSettings.GlobalizePath(path)}");
        }
    }
}
