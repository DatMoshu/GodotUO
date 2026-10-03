// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the login step, doing what the classic LoginGump
// does (account, password, the arrow, Quit, Credits, its boxes) with the
// gump's own pieces where the gump puts them, over its own painting.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Input.Touch;
using GUO.Input.Touch.Pregame;
using GUO.Input.Touch.Pregame.Accounts;
using GUO.Utility;

namespace GUO.Pregame3D;

internal sealed class LoginStage : Stage
{
    private const int MaxField = 30;

    private string _account = "";
    private string _password = "";
    private readonly List<(Control control, ShaderMaterial material)> _props = new();
    private readonly List<IOverlayFocusable> _items = new();
    private GumpProp _accountField, _passwordField, _arrow, _quit, _credits;
    private UiFocus _servers;
    private readonly GumpProp[] _boxes = new GumpProp[3];
    private Label _accountText, _passwordText;
    private PanelContainer _saved;
    private ServerEntry _server;
    private PanelContainer _creditsCard;

    public static string AccountForProbe => (PregameScreen.Instance?.Stage as LoginStage)?._account;

    public static bool ProbeOnAccount => PregameScreen.Instance?.Stage is LoginStage s && s.D.Focus.Current == s._accountField;
    public static bool ProbeOnPassword => PregameScreen.Instance?.Stage is LoginStage s && s.D.Focus.Current == s._passwordField;
    public static Control ProbeAccountField => (PregameScreen.Instance?.Stage as LoginStage)?._accountField?.Control;
    public static Control ProbePasswordField => (PregameScreen.Instance?.Stage as LoginStage)?._passwordField?.Control;
    public static bool ProbeOnLogin => PregameScreen.Instance?.Stage is LoginStage s && s.D.Focus.Current == s._arrow;

    public override IEnumerable<IOverlayFocusable> OverlayItems => _items;

    /// <summary>The painting's own text runs along its bottom: the hints go to the top here.</summary>
    public override bool HintsAtTop => true;

    public override string Hints => _creditsCard != null
        ? "B  Close"
        : D.Focus.Current == _accountField || D.Focus.Current == _passwordField
            ? "A  Type     Start  Login     LB  Servers     Y  Credits"
            : "A  Press     Start  Login     LB  Servers     Y  Credits";

    public override void Enter()
    {
        Settings s = Settings.GlobalSettings;

        if (_account.Length == 0)
        {
            _account = s.Username ?? "";
            _password = string.IsNullOrEmpty(s.Password) ? "" : Crypter.Decrypt(s.Password);
        }

        D.SetDim(1f);
        Painting art = D.Art;

        if (art.LoginPanel is Rect2 panel)
        {
            // Older clients: the stone login panel and its three labels, under the fields.
            PanelContainer p = Frame(0x13BE, panel.Size);
            Prop(p, panel.Position);
            Caption(GUO.Resources.ResGumps.LoginToUO, new Vector2(253, 305), UoTheme.Cream);
            Caption("Account Name", new Vector2(183, 345), UoTheme.Cream);
            Caption("Password", new Vector2(183, 385), UoTheme.Cream);
        }

        _accountField = Field(art.AccountField, out _accountText);
        _passwordField = Field(art.PasswordField, out _passwordText);
        _accountField.Tag = "account";
        _passwordField.Tag = "password";
        _accountField.Activated = () => Edit(true);
        _passwordField.Activated = () => Edit(false);

        _arrow = Button(art.Arrow, "login", DoLogin);
        _quit = Button(art.Quit, "quit", () => Client.Game.Exit());
        _credits = Button(art.Credits, "credits", ShowCredits);

        // The gump's three boxes: Autologin, Save account, Music, one after the other.
        string[] captions = { ResGumps("Autologin"), ResGumps("Save Account"), "Music" };
        Vector2 at = art.FirstCheckbox;

        for (int i = 0; i < 3; i++)
        {
            int index = i;
            _boxes[i] = new GumpProp(Painting.CheckboxOff, Painting.CheckboxOff, Painting.CheckboxOff, at) { Tag = "box" + i };
            _boxes[i].Activated = () => Toggle(index);
            AddProp(_boxes[i]);
            _items.Add(_boxes[i]);
            Label caption = Caption(captions[i], at + new Vector2(22, 1), UoTheme.Cream);
            float width = UoTheme.Font.GetStringSize(captions[i], HorizontalAlignment.Left, -1, UoTheme.FontSize).X;
            at += new Vector2(22 + width + 10, 0);
        }

        Caption($"UO Version {Settings.GlobalSettings.ClientVersion}.", art.VersionAt, new Color("d0c8b8"));
        RefreshBoxes();
        ShowFields();
        BuildServersEntry();
        BuildSaved();
        Link();

        D.Focus.Set(_account.Length > 0 ? _passwordField : _accountField);
    }

    public override void Exit()
    {
        CloseCredits();

        foreach ((Control control, ShaderMaterial material) in _props)
        {
            D.RemoveProp(control, material);
        }

        _props.Clear();
        _items.Clear();
        _saved?.QueueFree();
        _saved = null;
        _servers?.Control.QueueFree();
        _servers = null;
    }

    public override void Update(double delta)
    {
        // Gump art that was not in the atlas on the first frame.
        foreach (IOverlayFocusable i in _items)
        {
            if (i is GumpProp p && !p.Ready)
            {
                p.Show();
            }
        }
    }

    private static string ResGumps(string fallback) => fallback switch
    {
        "Autologin" => GUO.Resources.ResGumps.Autologin,
        "Save Account" => GUO.Resources.ResGumps.SaveAccount,
        _ => fallback,
    };

    // --- the pieces -------------------------------------------------------------------

    private void AddProp(GumpProp p)
    {
        D.AddProp(p.Control, p.Material);
        _props.Add((p.Control, p.Material));
    }

    private void Prop(Control c, Vector2 at)
    {
        ShaderMaterial m = GumpProp.NewMaterial();
        c.Material = m;
        c.Position = at;
        D.AddProp(c, m);
        _props.Add((c, m));
    }

    private static PanelContainer Frame(ushort first, Vector2 size)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Size = size, CustomMinimumSize = size };
        p.AddThemeStyleboxOverride("panel", UoTheme.Frame(first, 4));
        return p;
    }

    private Label Caption(string text, Vector2 at, Color color)
    {
        Label l = Overlay.Text(text, color);
        l.AddThemeConstantOverride("outline_size", 3);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        Prop(l, at);
        return l;
    }

    private GumpProp Field(Rect2 rect, out Label text)
    {
        // Exactly the gump's ResizePic size (a container would grow to its text).
        var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Size = rect.Size };
        frame.AddThemeStyleboxOverride("panel", Overlay.Frame(0x0BB8, 4));
        text = Overlay.Text("", new Color("1c1812"));
        text.UseParentMaterial = true;
        text.ClipText = true;
        text.Position = new Vector2(7, 0);
        text.Size = new Vector2(rect.Size.X - 14, rect.Size.Y);
        text.VerticalAlignment = VerticalAlignment.Center;
        frame.AddChild(text);
        var prop = new GumpProp(frame, rect.Position);
        AddProp(prop);
        _items.Add(prop);
        return prop;
    }

    private GumpProp Button(GumpButtonArt art, string tag, Action activated)
    {
        var b = new GumpProp(art.Normal, art.Pressed, art.Over, art.At) { Tag = tag, Activated = activated };
        AddProp(b);
        _items.Add(b);
        return b;
    }

    private void ShowFields()
    {
        _accountText.Text = _account;
        _passwordText.Text = new string('*', _password.Length);
    }

    // --- the neighbour graph (authored) ----------------------------------------------

    private void Link()
    {
        GumpProp a = _accountField, p = _passwordField, login = _arrow, quit = _quit, credits = _credits;

        a.Up = _savedItems.Count > 0 ? _savedItems[^1] : credits;
        a.Down = p; a.Left = quit; a.Right = credits;
        p.Up = a; p.Down = login; p.Left = quit; p.Right = credits;
        login.Up = p; login.Down = _boxes[1]; login.Left = quit; login.Right = credits;
        quit.Up = _servers; quit.Right = a; quit.Left = null; quit.Down = _boxes[0];
        _servers.Up = null; _servers.Down = quit; _servers.Left = null; _servers.Right = a;
        credits.Up = _savedItems.Count > 0 ? _savedItems[0] : null; credits.Left = a; credits.Right = null; credits.Down = p;

        for (int i = 0; i < _boxes.Length; i++)
        {
            _boxes[i].Up = login;
            _boxes[i].Down = null;
            _boxes[i].Left = i > 0 ? _boxes[i - 1] : quit;
            _boxes[i].Right = i + 1 < _boxes.Length ? _boxes[i + 1] : credits;
        }

        for (int i = 0; i < _savedItems.Count; i++)
        {
            _savedItems[i].Up = i > 0 ? _savedItems[i - 1] : null;
            _savedItems[i].Down = i + 1 < _savedItems.Count ? _savedItems[i + 1] : a;
            _savedItems[i].Left = null;
            _savedItems[i].Right = credits;
        }
    }

    // --- commands ---------------------------------------------------------------------

    public override bool Command(PadCmd cmd)
    {
        if (_creditsCard != null)
        {
            if (cmd is PadCmd.B or PadCmd.A or PadCmd.Start)
            {
                CloseCredits();
            }

            return true;
        }

        switch (cmd)
        {
            case PadCmd.Start:
                DoLogin();
                return true;

            case PadCmd.Y:
                ShowCredits();
                return true;

            case PadCmd.LeftShoulder:
                OpenServers();
                return true;

            case PadCmd.X:
                D.Focus.Set(_accountField);
                Edit(true);
                return true;

            case PadCmd.B:
                return true; // the first step: nothing to go back to
        }

        return false;
    }

    /// <summary>Typing on a focused field opens its keyboard with that letter.</summary>
    public override bool Key(InputEventKey k)
    {
        bool account = D.Focus.Current == _accountField, password = D.Focus.Current == _passwordField;

        if ((account || password) && ((k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed) || k.Keycode == Godot.Key.Backspace))
        {
            Edit(account);
            D.Keyboard.Key(k);
            return true;
        }

        return false;
    }

    private void Edit(bool account)
    {
        D.Keyboard.Open(
            account ? "Account name" : "Password",
            account ? _account : _password,
            secret: !account,
            MaxField,
            done: text =>
            {
                SetField(account, text);
                D.Focus.Set(account ? _passwordField : _arrow);
                D.RefreshHints();
            },
            cancel: () =>
            {
                ShowFields();
                D.RefreshHints();
            },
            changed: text => SetField(account, text),
            field: account ? _accountField.Control : _passwordField.Control,
            fieldText: account ? _accountText : _passwordText);
        D.RefreshHints();
    }

    private void SetField(bool account, string text)
    {
        if (account)
        {
            _account = text;
        }
        else
        {
            _password = text;
        }

        // The keyboard draws the field (with its caret) while it is open.
        if (!D.Keyboard.IsOpen)
        {
            ShowFields();
        }
    }

    private void DoLogin()
    {
        if (Login == null || Login.CurrentLoginStep != Game.Scenes.LoginSteps.Main)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_account))
        {
            D.Focus.Set(_accountField);
            Edit(true);
            return;
        }

        GD.Print($"[GUO] pregame3d: login as \"{_account}\" to {Settings.GlobalSettings.IP}:{Settings.GlobalSettings.Port}");
        Login.Connect(_account, _password);
    }

    // --- the classic gump's three boxes ------------------------------------------------

    private void Toggle(int i)
    {
        Settings s = Settings.GlobalSettings;

        switch (i)
        {
            case 0:
                s.AutoLogin = !s.AutoLogin;
                break;
            case 1:
                s.SaveAccount = !s.SaveAccount;
                break;
            case 2:
                s.LoginMusic = !s.LoginMusic;
                Client.Game.Audio?.UpdateCurrentMusicVolume(true);
                break;
        }

        s.Save();
        RefreshBoxes();
    }

    private void RefreshBoxes()
    {
        Settings s = Settings.GlobalSettings;
        bool[] on = { s.AutoLogin, s.SaveAccount, s.LoginMusic };

        for (int i = 0; i < 3; i++)
        {
            ushort id = on[i] ? Painting.CheckboxOn : Painting.CheckboxOff;
            _boxes[i].SetArt(id, id, id);
        }
    }

    // --- the server list (the classic pre-game card) -------------------------------------

    /// <summary>
    /// "Servers", top left: opens the same card the classic login screen's Servers button
    /// opens (ServerBook, ServerPlay.Check, the shard's own files and packs), over this one.
    /// </summary>
    private void BuildServersEntry()
    {
        Label l = Overlay.Text("Servers", UoTheme.Ink);
        _servers = Overlay.FrameRow(l, l, "servers");
        _servers.Pressed = OpenServers;
        _items.Add(_servers);
        D.OverlayRoot.AddChild(_servers.Control);
        _servers.Control.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.Minsize, 8);
    }

    private void OpenServers()
    {
        if (Login == null || Login.CurrentLoginStep != Game.Scenes.LoginSteps.Main)
        {
            return;
        }

        D.Keyboard.Close();
        GUO.Input.Touch.Pregame.PregameCard.OpenOnMain();
        D.RefreshHints();
    }

    /// <summary>For the probe: the card is up over the pregame.</summary>
    public static bool ProbeCardOpen => GUO.Input.Touch.Pregame.PregameCard.OnMain;

    public static bool ProbeOnServers => PregameScreen.Instance?.Stage is LoginStage s && s._servers != null && s.D.Focus.Current == s._servers;

    /// <summary>
    /// ServerPlay.Play at the login step, with no login gump: the card chose a server (its
    /// address is in the settings now). The card closes, the saved accounts are those of the
    /// new server, and a saved account chosen on the card logs in as the classic path does.
    /// Null when this is not the pregame's login step.
    /// </summary>
    public static string PlayOn(ServerEntry e, SavedAccount account)
    {
        if (PregameScreen.Instance?.Stage is not LoginStage s)
        {
            return null;
        }

        GUO.Input.Touch.Pregame.PregameCard.CloseOnMain();
        s.RefreshSaved();

        if (account == null)
        {
            s.D.Focus.Set(string.IsNullOrEmpty(s._account) ? s._accountField : s._passwordField);
            s.D.RefreshHints();

            return $"Type your account and password to log in to {e.Name}.";
        }

        s._server = e;
        s.PickAccount(account);

        return $"Logging in to {e.Name} as {account.Name}.";
    }

    // --- saved accounts ----------------------------------------------------------------

    /// <summary>The saved accounts of the server now in the settings (it changed on the card).</summary>
    private void RefreshSaved()
    {
        foreach (UiFocus f in _savedItems)
        {
            _items.Remove(f);
        }

        _saved?.QueueFree();
        _saved = null;
        BuildSaved();
        Link();
    }

    private readonly List<UiFocus> _savedItems = new();

    /// <summary>The server's saved accounts (AccountBook) as a parchment list: one press logs in.</summary>
    private void BuildSaved()
    {
        _savedItems.Clear();
        IReadOnlyList<SavedAccount> accounts;

        try
        {
            Settings s = Settings.GlobalSettings;
            _server = ServerBook.Find(s.IP, s.Port);
            accounts = _server == null ? Array.Empty<SavedAccount>() : AccountBook.For(_server);
        }
        catch (Exception ex)
        {
            // The keystore (libsecret) may be missing, e.g. a Deck in Game Mode: no list then.
            GD.PrintErr($"[GUO] pregame3d: saved accounts unavailable: {ex.Message}");
            return;
        }

        if (accounts.Count == 0)
        {
            return;
        }

        _saved = Overlay.Card(Overlay.Parchment);
        VBoxContainer col = Overlay.Column(1);
        _saved.AddChild(col);
        col.AddChild(Overlay.Text("Saved accounts", UoTheme.Heading));

        foreach (SavedAccount account in accounts)
        {
            SavedAccount a = account;
            var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            Label l = Overlay.Text(a.Name, UoTheme.Ink);
            row.AddChild(l);
            col.AddChild(row);
            var f = new UiFocus(row) { Tag = "saved:" + a.Name };
            f.Shown = on =>
            {
                row.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = on ? new Color(0.878f, 0.69f, 0.314f, 0.55f) : new Color(0, 0, 0, 0) });
                l.AddThemeColorOverride("font_color", on ? UoTheme.Danger : UoTheme.Ink);
            };
            f.Pressed = () => PickAccount(a);
            f.Shown(false);
            _savedItems.Add(f);
            _items.Add(f);
        }

        D.OverlayRoot.AddChild(_saved);
        _saved.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 8);
        _saved.GrowHorizontal = Control.GrowDirection.Begin;
    }

    /// <summary>A saved account: fill the fields and log in, as the pre-game card's Play does.</summary>
    private void PickAccount(SavedAccount account)
    {
        _account = account.Name;
        string password = null;
        string why = null;

        try
        {
            password = AccountBook.Password(_server, account, out why);
        }
        catch (Exception ex)
        {
            why = ex.Message;
        }

        if (password == null)
        {
            _password = "";
            ShowFields();
            D.Focus.Set(_passwordField);
            D.ShowMessage(account.HasPassword
                ? $"Couldn't read the saved password for {account.Name}: {why}. Type it instead."
                : $"Type the password for {account.Name}.", () => Edit(false));
            return;
        }

        _password = password;
        ShowFields();
        AccountBook.Touch(_server, account);
        Login.ManagedLogin = true;
        Login.Connect(_account, _password);
        Login.ManagedLogin = false;
    }

    // --- credits ----------------------------------------------------------------------

    private void ShowCredits()
    {
        if (_creditsCard != null)
        {
            return;
        }

        _creditsCard = Overlay.Card(Overlay.Stone);
        VBoxContainer col = Overlay.Column(6);
        col.AddChild(Overlay.Text("Credits", UoTheme.Heading, 2));
        Label body = Overlay.Text(PregameScreen.CreditsText, UoTheme.Ink, wrap: true);
        body.CustomMinimumSize = new Vector2(380, 0);
        col.AddChild(body);
        _creditsCard.AddChild(col);
        D.OverlayRoot.AddChild(_creditsCard);
        _creditsCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        _creditsCard.GrowHorizontal = Control.GrowDirection.Both;
        _creditsCard.GrowVertical = Control.GrowDirection.Both;
        D.RefreshHints();
    }

    private void CloseCredits()
    {
        _creditsCard?.QueueFree();
        _creditsCard = null;
        D?.RefreshHints();
    }
}
