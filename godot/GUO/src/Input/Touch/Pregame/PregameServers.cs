// GUO addition, not a port: upstream ClassicUO keeps no list of servers.

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Host;
using GUO.Input.Touch.Pregame.Accounts;
using GUO.Utility;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// The pre-game card's Servers tab (docs/ui/second_screen_pregame.md): the
/// list on parchment in groups (Favourites, Your servers, Recent, Community)
/// and the chosen server's page beside it, whose Play is the client's own
/// login arrow. A tap selects a row, a second tap on it plays. "Add server"
/// asks for a name, an address and a port; "Refresh" reads the catalogue
/// again and times every row anew. A row's dot is gold when the server
/// answers, hollow when it doesn't, red when GUO can't play there.
/// </summary>
/// <remarks>
/// A server's address is shown only for the player's own servers: a recent
/// or dev entry says where it came from instead, so a photograph of the
/// screen never carries an address the player did not type.
/// </remarks>
internal sealed partial class PregameServers : HBoxContainer
{
    private const int LineSpacing = -6;

    private static readonly Color Rule = new("5c554a");

    private readonly VBoxContainer _list;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _detail;
    private ScrollContainer _infoScroll;
    private VBoxContainer _info;
    private readonly Dictionary<ServerEntry, Button> _rows = new();
    private readonly Dictionary<ServerEntry, RowParts> _parts = new();
    private Label _pingNote;
    private double _sincePoll;
    private ServerEntry _selected;
    private ulong _lastTapMs;
    private ServerEntry _lastTapped;
    /// <summary>A question the page asks before a log-out or a restart; null when none.</summary>
    private sealed record Question(string Text, string Yes, float YesWidth, string No, Action Do);

    private Question _ask;
    private bool _adding;
    private LineEdit _addName, _addHost, _addPort;
    private bool _addingAccount;
    private SavedAccount _account;
    private ServerEntry _accountFor;
    private string _accountNames;
    private string _status = "";
    private bool _narrow;

    /// <summary>A play or a form wants the card to redraw its fields.</summary>
    public event Action Changed;

    public PregameServers()
    {
        AddThemeConstantOverride("separation", 6);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.1f };
        left.AddThemeConstantOverride("separation", 4);
        AddChild(left);

        var listPage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        listPage.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 5));
        left.AddChild(listPage);
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        listPage.AddChild(_scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 1);
        _scroll.AddChild(_list);

        var under = new HBoxContainer();
        under.AddThemeConstantOverride("separation", 4);
        left.AddChild(under);
        Button add = UoTheme.Button("Add server", 72);
        add.Pressed += () => { _adding = true; _addingAccount = false; _ask = null; ShowDetail(); };
        under.AddChild(add);
        AddButton = add;
        Button refresh = UoTheme.Button("Refresh", 52);
        refresh.Pressed += Refresh;
        under.AddChild(refresh);
        RefreshButton = refresh;

        var detailPage = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        detailPage.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 6));
        AddChild(detailPage);
        _detail = new VBoxContainer();
        _detail.AddThemeConstantOverride("separation", 4);
        detailPage.AddChild(_detail);

        Rebuild();
    }

    public void SetNarrow(bool narrow)
    {
        if (narrow != _narrow)
        {
            foreach ((ServerEntry e, RowParts parts) in _parts)
            {
                parts.Era.Visible = !narrow && !string.IsNullOrWhiteSpace(e.Era);
            }
        }

        _narrow = narrow;
        AddThemeConstantOverride("separation", narrow ? 4 : 6);
    }

    // --- the list ------------------------------------------------------------------

    /// <summary>The list again from the book (after a play, an add, a favourite).</summary>
    public void Rebuild()
    {
        foreach (Node n in _list.GetChildren())
        {
            _list.RemoveChild(n);
            n.QueueFree();
        }

        _rows.Clear();
        _parts.Clear();
        var shown = new List<ServerEntry>();
        BackButton = null;
        DevButtons.Clear();

        // A dev build: one click logs in as a dev account (or switches to it from the world).
        IReadOnlyList<SavedAccount> dev = DevLogin.Accounts;

        if (dev.Count > 0)
        {
            _list.AddChild(Head("Dev logins"));
            var devRow = new HFlowContainer();
            devRow.AddThemeConstantOverride("h_separation", 4);
            devRow.AddThemeConstantOverride("v_separation", 3);

            foreach (SavedAccount a in dev.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool playing = ServerPlay.InWorld && DevLogin.InUse == a;
                Button b = UoTheme.Button(playing ? a.Name + " (playing)" : ServerPlay.InWorld ? "Switch to " + a.Name : a.Name, 44);
                b.Disabled = playing;
                b.Pressed += () => { DevLogin.Start(a); _status = DevLogin.Status; ShowDetail(); };
                devRow.AddChild(b);
                DevButtons[a.Name] = b;
            }

            _list.AddChild(devRow);
        }

        // This run plays with a shard's own files: say so, and the way back.
        if (ShardSession.Active)
        {
            _list.AddChild(Note($"GUO is running with {ShardSession.Current.Name}'s {ShardSession.Holding}.", UoTheme.Ink));
            Button back = UoTheme.Button("Your own files", 72);
            back.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            back.Pressed += () => Ask(new Question("Restart GUO with your own files?", "Restart GUO", 60, "Not now", () => ShardSession.End()));
            _list.AddChild(back);
            BackButton = back;
        }
        else if (ShardSession.Dropped != null)
        {
            _list.AddChild(Note(ShardSession.Dropped, UoTheme.Danger));
        }

        Group("Favourites", ServerBook.Favourites, shown);
        Group("Your servers", ServerBook.Own, shown);
        Group("Recent", ServerBook.Recent, shown);

        if (shown.Count == 0)
        {
            _list.AddChild(Note("No servers yet. Add the one you play on, or log in on the login screen and it will be kept here."));
        }

        // Community: the catalogue, less what the player already keeps.
        if (!ServerCatalogue.Loaded && ServerCatalogue.Servers.Count == 0)
        {
            _list.AddChild(Head("Community"));
            _list.AddChild(Note("The server list couldn't be loaded. Your saved servers are above. Refresh to try again."));
        }
        else if (!Group("Community", ServerCatalogue.Servers.Select(c => ServerBook.Find(c.Host, c.Port) ?? c), shown))
        {
            _list.AddChild(Head("Community"));
            _list.AddChild(Note("No community shards are listed yet."));
        }

        // Keep the choice when it is still listed; else the server in use, else the first.
        if (_selected == null || !shown.Contains(_selected))
        {
            Configuration.Settings s = Configuration.Settings.GlobalSettings;
            _selected = shown.FirstOrDefault(e => e.Same(s.IP, s.Port)) ?? shown.FirstOrDefault();
        }

        Select(_selected, false);
    }

    /// <summary>A group of rows under its name; false when it had none to list.</summary>
    private bool Group(string name, IEnumerable<ServerEntry> entries, List<ServerEntry> shown)
    {
        // Once per address: a catalogue shard the player keeps lists where they keep it.
        List<ServerEntry> list = entries.Where(e => !shown.Any(x => x == e || x.Same(e.Host, e.Port))).ToList();

        if (list.Count == 0)
        {
            return false;
        }

        _list.AddChild(Head(name));

        foreach (ServerEntry e in list)
        {
            shown.Add(e);
            _list.AddChild(Row(e));
        }

        return true;
    }

    /// <summary>The group's name in Muted over one art pixel of rule.</summary>
    private static Control Head(string name)
    {
        var head = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 0);
        head.AddChild(UoTheme.Label(name, UoTheme.Muted));
        head.AddChild(new ColorRect { Color = Rule, CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore });
        return head;
    }

    private sealed class RowParts
    {
        public Label Name, Era, Ping;
        public PingDot Dot;
        public ServerPing.Result Shown = new(ServerPing.Kind.Unknown, -1, 0, false);
    }

    private Button Row(ServerEntry e)
    {
        // Flat: a row of the list, not a plate. The chosen one sits on gold.
        // Name, era, the last timing and the status dot, left to right.
        var b = new Button
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 18),
            FocusMode = FocusModeEnum.All,
            ClipContents = true,
        };

        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
        {
            b.AddThemeStyleboxOverride(state, new StyleBoxEmpty { ContentMarginLeft = 3, ContentMarginRight = 3 });
        }

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 3;
        line.OffsetRight = -3;
        line.AddThemeConstantOverride("separation", 4);
        b.AddChild(line);

        var parts = new RowParts
        {
            Name = UoTheme.Label(e.Name, UoTheme.Ink),
            Era = UoTheme.Label(e.Era ?? "", UoTheme.Muted),
            Ping = UoTheme.Label("", UoTheme.Muted),
            Dot = new PingDot(),
        };
        parts.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        parts.Name.ClipText = true;
        parts.Era.Visible = !_narrow && !string.IsNullOrWhiteSpace(e.Era);

        foreach (Control c in new Control[] { parts.Name, parts.Era, parts.Ping, parts.Dot })
        {
            c.MouseFilter = MouseFilterEnum.Ignore;
            c.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            line.AddChild(c);
        }

        // Red only where GUO can't play at all: one that needs its own files can, after a restart.
        parts.Dot.Blocked = ServerPlay.Check(e, out _) == ServerPlay.Verdict.NotAllowed;
        b.Pressed += () => Tapped(e);
        _rows[e] = b;
        _parts[e] = parts;
        ShowPing(e, parts);

        return b;
    }

    // --- live status -----------------------------------------------------------------

    public override void _Process(double delta)
    {
        _sincePoll += delta;

        // Four times a second, and only while the tab is in front of the player.
        if (_sincePoll < 0.25 || !PregameCard.Shown || !IsVisibleInTree())
        {
            return;
        }

        _sincePoll = 0;

        foreach ((ServerEntry e, RowParts parts) in _parts)
        {
            ServerPing.Want(e);
            ShowPing(e, parts);
        }
    }

    private void ShowPing(ServerEntry e, RowParts parts)
    {
        ServerPing.Result r = ServerPing.Get(e);

        if (r == parts.Shown)
        {
            return;
        }

        parts.Shown = r;
        parts.Ping.Text = r.Kind == ServerPing.Kind.Down ? "\u2014" : r.Ms > 0 ? $"{r.Ms} ms" : "";
        parts.Dot.Kind = r.Kind;

        if (e == _selected && _pingNote != null)
        {
            _pingNote.Text = PingText(e, r);
            _pingNote.AddThemeColorOverride("font_color", r.Kind == ServerPing.Kind.Down ? UoTheme.Danger : UoTheme.Muted);
        }
    }

    private static string PingText(ServerEntry e, ServerPing.Result r) => r.Kind switch
    {
        ServerPing.Kind.Down => $"{e.Name} isn't answering (no reply in {ServerPing.TimeoutMs / 1000} s). It may be down, or the address is wrong.",
        ServerPing.Kind.Up => $"Answering, {r.Ms} ms.",
        _ => "Checking whether it answers...",
    };

    /// <summary>Refresh: the catalogue read again, every row timed anew.</summary>
    private void Refresh()
    {
        ServerCatalogue.Load();
        Rebuild();

        foreach (ServerEntry e in _parts.Keys)
        {
            ServerPing.Want(e, true);
        }

        GD.Print($"[GUO] pregame card: refresh, {_parts.Count} servers");
    }

    private void Tapped(ServerEntry e)
    {
        ulong now = Godot.Time.GetTicksMsec();
        bool second = e == _lastTapped && now - _lastTapMs < 600;
        _lastTapped = e;
        _lastTapMs = now;

        if (second && e == _selected && !_adding)
        {
            PlayPressed();
            return;
        }

        _adding = false;
        _addingAccount = false;
        _ask = null;
        _status = "";
        Select(e, true);
    }

    private void Select(ServerEntry e, bool log)
    {
        _selected = e;

        foreach ((ServerEntry entry, Button b) in _rows)
        {
            bool on = entry == e;
            StyleBox box = on
                ? new StyleBoxFlat { BgColor = new Color(UoTheme.Gold, 0.55f), ContentMarginLeft = 3, ContentMarginRight = 3 }
                : new StyleBoxEmpty { ContentMarginLeft = 3, ContentMarginRight = 3 };
            b.AddThemeStyleboxOverride("normal", box);
            b.AddThemeStyleboxOverride("hover", box);

            if (_parts.TryGetValue(entry, out RowParts parts))
            {
                parts.Name.AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
            }
        }

        ShowDetail();

        if (log && e != null)
        {
            GD.Print($"[GUO] pregame card: server \"{e.Name}\"");
        }
    }

    // --- the page ------------------------------------------------------------------

    private void ShowDetail()
    {
        foreach (Node n in _detail.GetChildren())
        {
            _detail.RemoveChild(n);
            n.QueueFree();
        }

        PlayButton = null;
        ConfirmButton = null;
        SiteButton = null;
        FilesButton = null;
        NoButton = null;
        AddAccountButton = null;
        ForgetAccountButton = null;
        AccountButtons.Clear();
        _pingNote = null;

        // What there is to read scrolls; Play and the actions stay at the
        // bottom of the page, in view on the smallest card.
        _infoScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _info.AddThemeConstantOverride("separation", 4);
        _infoScroll.AddChild(_info);
        _detail.AddChild(_infoScroll);

        if (_adding)
        {
            AddForm();
            return;
        }

        ServerEntry e = _selected;

        if (e != null && _addingAccount)
        {
            AccountForm(e);
            return;
        }

        if (e == null)
        {
            if (_ask != null)
            {
                AskRow();
                return;
            }

            _info.AddChild(Note("Choose a server on the left, or add one."));
            return;
        }

        _info.AddChild(UoTheme.Label(e.Name, UoTheme.Heading));

        string kind = string.Join(", ", new[] { e.Emulator, e.Era }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (kind.Length > 0)
        {
            _info.AddChild(Note(kind));
        }

        if (e.Own)
        {
            _info.AddChild(Note($"{e.Host.Trim()}, port {e.Port}"));
        }
        else if (e.Dev)
        {
            _info.AddChild(Note(e.Description));
        }
        else if (!string.IsNullOrWhiteSpace(e.Description))
        {
            _info.AddChild(Note(e.Description));
        }

        ServerPing.Result ping = ServerPing.Get(e);
        _pingNote = Note(PingText(e, ping), ping.Kind == ServerPing.Kind.Down ? UoTheme.Danger : UoTheme.Muted);
        _info.AddChild(_pingNote);

        if (e.LastPlayed is DateTime played)
        {
            _info.AddChild(Note("Last played " + Ago(played)));
        }

        Configuration.Settings s = Configuration.Settings.GlobalSettings;

        if (e.Same(s.IP, s.Port))
        {
            _info.AddChild(Note(ServerPlay.InWorld ? "You're playing here now." : "The login screen is set to this server."));
        }

        ServerPlay.Verdict verdict = ServerPlay.Check(e, out string reason);

        if (verdict == ServerPlay.Verdict.NotAllowed)
        {
            _info.AddChild(Note(reason, UoTheme.Danger));
        }
        else if (verdict is ServerPlay.Verdict.NeedsOwnData or ServerPlay.Verdict.NeedsContent)
        {
            _info.AddChild(Note(reason, UoTheme.Ink));
        }

        if (_contentStatus != null && _contentFor == e)
        {
            _info.AddChild(Note(_contentStatus, _contentFailed ? UoTheme.Danger : UoTheme.Ink));
        }

        if (ShardSession.IsFor(e))
        {
            _info.AddChild(Note($"GUO is running with its {ShardSession.Holding} now."));
        }

        if (_ask != null)
        {
            AskRow();
            return;
        }

        if (verdict != ServerPlay.Verdict.NotAllowed)
        {
            AccountsBlock(e);
        }

        // Play: the login gump's own arrow, the one bright thing on the card.
        var play = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        play.AddThemeConstantOverride("separation", 6);
        PlayButton = Arrow(verdict != ServerPlay.Verdict.NotAllowed);
        PlayButton.Pressed += PlayPressed;
        play.AddChild(PlayButton);
        PlayButton.TooltipText = "Play";

        // The newer clients' arrow is a plate that says Login; the older one is a bare arrow and gets its word.
        if (Client.Game.UO.Version < Utility.ClientVersion.CV_706400)
        {
            play.AddChild(UoTheme.Label("Play", verdict != ServerPlay.Verdict.NotAllowed ? UoTheme.Heading : UoTheme.Muted));
        }
        _detail.AddChild(play);

        if (_status.Length > 0)
        {
            _detail.AddChild(Note(_status, UoTheme.Ink));
        }

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 4);
        actions.AddThemeConstantOverride("v_separation", 3);

        if (!e.Dev)
        {
            Button fav = UoTheme.Button(e.Favourite ? "Unfavourite" : "Favourite", 56);
            fav.Pressed += () => { _selected = ServerBook.SetFavourite(e, !e.Favourite); Rebuild(); };
            actions.AddChild(fav);
            FavouriteButton = fav;
        }

        if (e.Own || (!e.Favourite && !e.Dev && !ServerCatalogue.Servers.Contains(e)))
        {
            Button forget = UoTheme.Button("Forget", 44);
            forget.Pressed += () => { ServerBook.Remove(e); _selected = null; Rebuild(); };
            actions.AddChild(forget);
        }

        // A shard played with its own files: where they are can change.
        if (!e.Dev && !string.IsNullOrWhiteSpace(e.DataFolder) && (verdict == ServerPlay.Verdict.NeedsOwnData || ShardSession.IsFor(e)))
        {
            Button files = UoTheme.Button("Change files", 56);
            files.Pressed += () => ChooseFiles(e, false);
            actions.AddChild(files);
            FilesButton = files;
        }

        // A catalogue shard's own page, in the browser.
        if (!string.IsNullOrWhiteSpace(e.Site) && e.Site.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Button site = UoTheme.Button("Site", 36);
            site.Pressed += () => OS.ShellOpen(e.Site.Trim());
            actions.AddChild(site);
            SiteButton = site;
        }

        if (actions.GetChildCount() > 0)
        {
            _detail.AddChild(actions);
        }
    }

    private static TextureButton Arrow(bool enabled)
    {
        // As LoginGump picks its arrow: the older art before 7.0.64.0.
        bool old = Client.Game.UO.Version < Utility.ClientVersion.CV_706400;
        return new TextureButton
        {
            TextureNormal = UoTheme.GumpTexture(old ? (ushort) 0x15A4 : (ushort) 0x5CD),
            TexturePressed = UoTheme.GumpTexture(old ? (ushort) 0x15A6 : (ushort) 0x5CC),
            TextureHover = UoTheme.GumpTexture(old ? (ushort) 0x15A5 : (ushort) 0x5CB),
            TextureDisabled = UoTheme.GumpTexture(old ? (ushort) 0x15A4 : (ushort) 0x5CD),
            Disabled = !enabled,
            SelfModulate = enabled ? Colors.White : new Color(1, 1, 1, 0.45f),
            TextureFilter = TextureFilterEnum.Nearest,
            FocusMode = FocusModeEnum.All,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
    }

    private void PlayPressed()
    {
        ServerEntry e = _selected;

        if (e == null)
        {
            return;
        }

        ServerPlay.Verdict verdict = ServerPlay.Check(e, out _);

        if (verdict == ServerPlay.Verdict.NotAllowed)
        {
            return;
        }

        // A restart ends a session in the world: the question says so.
        string leaving = ServerPlay.InWorld ? " You'll be logged out." : "";

        // Its own files: picked once, then GUO restarts with them.
        if (verdict == ServerPlay.Verdict.NeedsOwnData)
        {
            if (string.IsNullOrWhiteSpace(e.DataFolder))
            {
                ChooseFiles(e, true);
                return;
            }

            Ask(new Question($"Restart GUO with {e.Name}'s files?{leaving}", "Restart GUO", 60, "Not now", () => ShardSession.Start(e)));
            return;
        }

        // Its packs: read what it names, ask, install, then restart with them mounted.
        if (verdict == ServerPlay.Verdict.NeedsContent)
        {
            _ = PrepareContent(e, leaving);
            return;
        }

        // Running with another shard's files: back to the player's own first.
        if (ShardSession.Active && !ShardSession.IsFor(e))
        {
            Ask(new Question($"Restart GUO with your own files and play on {e.Name}?{leaving}", "Restart GUO", 60, "Not now", () => ShardSession.End(e)));
            return;
        }

        if (ServerPlay.InWorld)
        {
            // Narrow: the question already names the server.
            Ask(new Question($"Log out and play on {e.Name}?", _narrow ? "Log out" : "Log out and play", _narrow ? 44 : 64, "Stay", () => DoPlay(e)));
            return;
        }

        DoPlay(e);
    }

    // The content step's progress line, shown on the card of the server it is for.
    private ServerEntry _contentFor;
    private string _contentStatus;
    private bool _contentFailed;
    private bool _contentBusy;

    private void ContentStatus(ServerEntry e, string text, bool failed = false)
    {
        _contentFor = e; _contentStatus = text; _contentFailed = failed;
        ServerPlayStatus(text);
        if (IsInsideTree() && _selected == e) ShowDetail();
    }

    private static void ServerPlayStatus(string text) => GD.Print("[GUO] servers: content: " + text);

    /// <summary>Reads the shard's descriptor and asks; on yes installs its packs and restarts with them.</summary>
    private async System.Threading.Tasks.Task PrepareContent(ServerEntry e, string leaving)
    {
        if (_contentBusy) return;
        _contentBusy = true;
        try
        {
            ContentStatus(e, "Reading what " + e.Name + " needs…");
            var content = await GUO.Store.StoreShardContent.Fetch(e.Content);
            ContentStatus(e, content.Summary() + ".");
            Ask(new Question($"Install {e.Name}'s packs and restart GUO?{leaving}", "Install and restart", 76, "Not now", () => _ = InstallContent(e, content)));
        }
        catch (System.Exception ex)
        {
            ContentStatus(e, "Couldn't read " + e.Name + "'s content: " + ex.Message, true);
        }
        finally { _contentBusy = false; }
    }

    private async System.Threading.Tasks.Task InstallContent(ServerEntry e, GUO.Store.StoreShardContent content)
    {
        if (_contentBusy) return;
        _contentBusy = true;
        try
        {
            string lockPath = await content.Prepare(GUO.Store.StoreOptions.Root, GUO.Store.StoreOptions.Trust,
                Configuration.PlatformDefaults.CurrentVersion, text => Callable.From(() => ContentStatus(e, text)).CallDeferred());
            ShardSession.Start(e, content, lockPath);
        }
        catch (System.Exception ex)
        {
            ContentStatus(e, "Couldn't install " + e.Name + "'s packs: " + ex.Message, true);
        }
        finally { _contentBusy = false; }
    }

    private void Ask(Question q)
    {
        _ask = q;
        _adding = false;
        ShowDetail();
    }

    /// <summary>The question, its yes in Danger, and its no.</summary>
    private void AskRow()
    {
        Question q = _ask;
        _detail.AddChild(Note(q.Text, UoTheme.Ink));
        var yesNo = new HFlowContainer();
        yesNo.AddThemeConstantOverride("h_separation", 4);
        yesNo.AddThemeConstantOverride("v_separation", 3);
        Button yes = UoTheme.Button(q.Yes, q.YesWidth);
        yes.AddThemeColorOverride("font_color", UoTheme.Danger);
        yes.Pressed += () =>
        {
            _ask = null;
            q.Do();

            // A restart may be held back (the probe): the page comes back.
            if (IsInsideTree())
            {
                ShowDetail();
            }
        };
        Button no = UoTheme.Button(q.No, 40);
        no.Pressed += () => { _ask = null; ShowDetail(); };
        yesNo.AddChild(yes);
        yesNo.AddChild(no);
        _detail.AddChild(yesNo);
        ConfirmButton = yes;
        NoButton = no;
    }

    /// <summary>
    /// The first-run screen, for the folder of <paramref name="e"/>'s own
    /// client files; kept in servers.json. Then, from Play, the restart question.
    /// </summary>
    private void ChooseFiles(ServerEntry e, bool thenPlay)
    {
        GD.Print($"[GUO] pregame card: choose files for \"{e.Name}\"");
        Picker = FirstRunScreen.OpenForShard(e.Name, folder =>
        {
            _selected = ServerBook.SetDataFolder(e, folder);
            GD.Print($"[GUO] pregame card: files for \"{_selected.Name}\" kept");
            Rebuild();

            if (thenPlay)
            {
                PlayPressed();
            }
        });
    }

    private void DoPlay(ServerEntry e)
    {
        ServerPlay.Play(e, _account);
        _status = ServerPlay.LastOutcome;
        Rebuild();
        Changed?.Invoke();
    }

    // --- accounts -------------------------------------------------------------------

    /// <summary>The server's saved accounts: a tap picks the one Play logs in as.</summary>
    private void AccountsBlock(ServerEntry e)
    {
        IReadOnlyList<SavedAccount> accounts = AccountBook.For(e);

        // A new page, or a changed list, picks the last used; the player can pick another, or none.
        string listed = string.Join("|", accounts.Select(a => a.Name));

        if (_accountFor == null || !_accountFor.Same(e.Host, e.Port) || listed != _accountNames)
        {
            _accountFor = e;
            _accountNames = listed;
            _account = accounts.FirstOrDefault();
        }

        _info.AddChild(UoTheme.Label("Accounts", UoTheme.Heading));

        if (accounts.Count == 0)
        {
            _info.AddChild(Note("None saved. Play uses what's typed on the login screen."));
        }

        var names = new HFlowContainer();
        names.AddThemeConstantOverride("h_separation", 4);
        names.AddThemeConstantOverride("v_separation", 3);

        foreach (SavedAccount a in accounts)
        {
            bool picked = a == _account;
            Button b = UoTheme.Button(a.Dev ? a.Name + " (dev)" : a.Name, 44);
            b.AddThemeColorOverride("font_color", picked ? UoTheme.Heading : UoTheme.Ink);

            if (picked)
            {
                b.AddThemeStyleboxOverride("normal", UoTheme.Plate(UoTheme.SelectedShade));
            }

            b.TooltipText = a.HasPassword ? "Password saved" : "No password saved";
            b.Pressed += () => { _account = picked ? null : a; ShowDetail(); };
            names.AddChild(b);
            AccountButtons[a.Name] = b;
        }

        Button add = UoTheme.Button("Add account", 60);
        add.Pressed += () => { _addingAccount = true; _ask = null; ShowDetail(); };
        names.AddChild(add);
        AddAccountButton = add;
        _info.AddChild(names);

        if (_account != null)
        {
            _info.AddChild(Note(_account.HasPassword
                ? $"Play logs in as {_account.Name}."
                : $"Play fills in {_account.Name}; type the password on the login screen."));
            Button forget = UoTheme.Button($"Forget {_account.Name}", 60);
            SavedAccount gone = _account;
            forget.Pressed += () => { AccountBook.Forget(e, gone); _account = null; _accountFor = null; ShowDetail(); };
            forget.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            _info.AddChild(forget);
            ForgetAccountButton = forget;
        }
    }

    private void AccountForm(ServerEntry e)
    {
        ISecretStore store = SecretStore.Current;
        _info.AddChild(UoTheme.Label($"Add an account for {e.Name}", UoTheme.Heading));
        _info.AddChild(UoTheme.Label("Account", UoTheme.Muted));
        _info.AddChild(AccountField = Field("Account name"));
        _info.AddChild(UoTheme.Label("Password", UoTheme.Muted));
        _info.AddChild(PasswordField = Field(""));
        PasswordField.Secret = true;
        KeepBox = null;

        if (store.Available)
        {
            KeepBox = new CheckBox { Text = "Save password", ButtonPressed = true, FocusMode = FocusModeEnum.All };
            _info.AddChild(KeepBox);

            // A dev build's dev shard: an account for the one-click dev logins.
            DevBox = null;

            if (e.Dev)
            {
                DevBox = new CheckBox { Text = "Dev account (one-click login)", FocusMode = FocusModeEnum.All };
                _info.AddChild(DevBox);
            }

            _info.AddChild(Note("Kept encrypted by this system's keystore. The classic Save account box on the login screen still stores a typed password the upstream way."));
        }
        else
        {
            _info.AddChild(Note(store is NoStore none ? none.Why : "Passwords aren't saved on this system.", UoTheme.Ink));
        }

        Label error = Note("", UoTheme.Danger);
        _info.AddChild(error);

        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 4);
        Button save = UoTheme.Button("Save", 44);
        save.Pressed += () =>
        {
            bool keep = KeepBox?.ButtonPressed ?? false;
            SavedAccount a = AccountBook.Add(e, AccountField.Text, PasswordField.Text, keep, out string why, DevBox?.ButtonPressed ?? false);
            PasswordField.Text = "";

            if (a == null)
            {
                error.Text = why;
                return;
            }

            _addingAccount = false;
            _account = a;
            _accountFor = e;
            _status = keep && !a.HasPassword ? $"Saved {a.Name} without its password: {why}." : "";
            ShowDetail();
        };
        Button cancel = UoTheme.Button("Cancel", 44);
        cancel.Pressed += () => { _addingAccount = false; PasswordField.Text = ""; ShowDetail(); };
        buttons.AddChild(save);
        buttons.AddChild(cancel);
        _detail.AddChild(buttons);
        AccountSaveButton = save;
    }

    private void AddForm()
    {
        _info.AddChild(UoTheme.Label("Add server", UoTheme.Heading));
        _info.AddChild(UoTheme.Label("Name", UoTheme.Muted));
        _info.AddChild(_addName = Field("My shard"));
        _info.AddChild(UoTheme.Label("Address", UoTheme.Muted));
        _info.AddChild(_addHost = Field("play.example.com"));
        _info.AddChild(UoTheme.Label("Port", UoTheme.Muted));
        _info.AddChild(_addPort = Field("2593"));
        _addPort.Text = "2593";

        Label error = Note("", UoTheme.Danger);
        _info.AddChild(error);

        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 4);
        Button save = UoTheme.Button("Save", 44);
        save.Pressed += () =>
        {
            ServerEntry added = ServerBook.Add(_addName.Text, _addHost.Text, _addPort.Text, out string why);

            if (added == null)
            {
                error.Text = why;
                return;
            }

            _adding = false;
            _selected = added;
            GD.Print($"[GUO] pregame card: added server \"{added.Name}\"");
            Rebuild();
        };
        Button cancel = UoTheme.Button("Cancel", 44);
        cancel.Pressed += () => { _adding = false; ShowDetail(); };
        buttons.AddChild(save);
        buttons.AddChild(cancel);
        _detail.AddChild(buttons);
        SaveButton = save;
    }

    private static LineEdit Field(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        CustomMinimumSize = new Vector2(0, 18),
    };

    private static Label Note(string text, Color? color = null)
    {
        Label l = UoTheme.Label(text, color ?? UoTheme.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.AddThemeConstantOverride("line_spacing", LineSpacing);
        return l;
    }

    private static string Ago(DateTime utc)
    {
        TimeSpan t = DateTime.UtcNow - utc;

        return t.TotalMinutes < 2 ? "just now"
            : t.TotalHours < 1 ? $"{(int) t.TotalMinutes} minutes ago"
            : t.TotalDays < 1 ? $"{(int) t.TotalHours} hours ago"
            : t.TotalDays < 2 ? "yesterday"
            : $"{(int) t.TotalDays} days ago";
    }

    /// <summary>A drag scrolls the list, or the page when it started over the page (<paramref name="at"/>, viewport pixels).</summary>
    public void ScrollBy(float artPixels, Vector2 at)
    {
        ScrollContainer target = _infoScroll != null && _infoScroll.GetGlobalRect().HasPoint(at) ? _infoScroll : _scroll;
        target.ScrollVertical += (int) artPixels;
    }

    // --- for the probe ------------------------------------------------------------------

    public ServerEntry Selected => _selected;
    public Button RowFor(ServerEntry e) => _rows.TryGetValue(e, out Button b) ? b : null;
    public IEnumerable<ServerEntry> Listed => _rows.Keys;
    public TextureButton PlayButton { get; private set; }
    public Button AddButton { get; }
    public Button RefreshButton { get; }
    public Button SiteButton { get; private set; }

    /// <summary>A row's dot and timing text as the player sees them.</summary>
    public (ServerPing.Kind Dot, bool Red, string Ping) RowStatus(ServerEntry e) =>
        _parts.TryGetValue(e, out RowParts p) ? (p.Dot.Kind, p.Dot.Blocked, p.Ping.Text) : default;
    public Button SaveButton { get; private set; }
    public Button FavouriteButton { get; private set; }
    public Button ConfirmButton { get; private set; }
    public Button NoButton { get; private set; }
    public Button FilesButton { get; private set; }
    public Button BackButton { get; private set; }
    public Button AddAccountButton { get; private set; }
    public Button AccountSaveButton { get; private set; }
    public Button ForgetAccountButton { get; private set; }
    public LineEdit AccountField { get; private set; }
    public LineEdit PasswordField { get; private set; }
    public CheckBox KeepBox { get; private set; }
    public CheckBox DevBox { get; private set; }
    public Dictionary<string, Button> DevButtons { get; } = new();
    public Dictionary<string, Button> AccountButtons { get; } = new();
    public SavedAccount PickedAccount => _account;
    public FirstRunScreen Picker { get; private set; }
    public LineEdit[] AddFields => new[] { _addName, _addHost, _addPort };
    public string Status => _status;
    public bool Adding => _adding;

    /// <summary>The detail page's text, joined.</summary>
    public string DetailText => string.Join(" | ", _detail.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text).Where(t => t.Length > 0));
}

/// <summary>
/// A row's status dot, five art pixels across: gold filled when the server
/// answers, a hollow ring when it doesn't, red when GUO can't play there;
/// nothing until the first timing lands.
/// </summary>
internal sealed partial class PingDot : Control
{
    private static readonly Color Down = new("5c554a");
    private static readonly Color Lit = new("c8961e");

    private ServerPing.Kind _kind;
    private bool _blocked;

    public PingDot() => CustomMinimumSize = new Vector2(5, 5);

    public ServerPing.Kind Kind
    {
        get => _kind;
        set
        {
            if (_kind != value)
            {
                _kind = value;
                QueueRedraw();
            }
        }
    }

    public bool Blocked
    {
        get => _blocked;
        set
        {
            _blocked = value;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_blocked)
        {
            Disc(UoTheme.Danger, true);
        }
        else if (_kind == ServerPing.Kind.Up)
        {
            Disc(Lit, true);
        }
        else if (_kind == ServerPing.Kind.Down)
        {
            Disc(Down, false);
        }
    }

    private void Disc(Color c, bool filled)
    {
        // Whole art pixels: a 3-5-5-5-3 disc, or its ring.
        DrawRect(new Rect2(1, 0, 3, 1), c);
        DrawRect(new Rect2(1, 4, 3, 1), c);
        DrawRect(new Rect2(0, 1, 1, 3), c);
        DrawRect(new Rect2(4, 1, 1, 3), c);

        if (filled)
        {
            DrawRect(new Rect2(1, 1, 3, 3), c);
        }
    }
}
