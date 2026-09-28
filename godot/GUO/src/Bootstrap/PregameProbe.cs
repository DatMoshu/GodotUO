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
/// double tap setting the address, the verdicts, Recent. Settings: the tabs
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
        }
        finally
        {
            gs.IP = ipWas;
            gs.Port = portWas;
            gs.Save();
            ServerBook.PathOverride = null;
            ServerBook.Load();
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

        if (world && _second)
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

    private static bool _second;

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
        ServerBook.PathOverride = ProjectSettings.GlobalizePath("user://probe_servers.json");
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
        if (!_second)
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
