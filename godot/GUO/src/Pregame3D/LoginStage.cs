// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the login step of the 3D pregame, doing what the
// classic LoginGump does (account, password, Login, Quit, Credits, its three
// boxes) with the chest's own objects.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Input.Touch.Pregame;
using GUO.Input.Touch.Pregame.Accounts;
using GUO.Utility;

namespace GUO.Pregame3D;

internal sealed class LoginStage : Stage
{
    private const int MaxField = 30;

    private string _account = "";
    private string _password = "";
    private Label3D _accountText, _passwordText, _loginText, _quitText, _creditsText;
    private readonly Label3D[] _studText = new Label3D[3];
    private readonly List<Hotspot> _cards = new();
    private readonly List<SavedAccount> _cardAccounts = new();
    private ServerEntry _server;
    private bool _opened;
    private PanelContainer _credits;

    public static string AccountForProbe => (PregameDiorama.Instance?.Stage as LoginStage)?._account;

    public override string Hints => _credits != null
        ? "B  Close"
        : D.Focus.Current == D.FieldAccount || D.Focus.Current == D.FieldPassword
            ? "A  Type     Start  Login     Y  Credits"
            : "A  Press     Start  Login     Y  Credits";

    public override void Enter()
    {
        Settings s = Settings.GlobalSettings;

        if (_account.Length == 0)
        {
            _account = s.Username ?? "";
            _password = string.IsNullOrEmpty(s.Password) ? "" : Crypter.Decrypt(s.Password);
        }

        ShowProps(true);
        D.Frame(D.LoginPose, new Node3D[] { D.Scene.ChestBody, D.Scene.Plaque, D.LoginButton }, _opened ? 0.7 : 0.01);
        D.Scene.SetGutter(1f);

        // The lid creaks open on boot, and again on the way back from a login.
        D.LidTo(1f, _opened ? 0.8 : 1.8);
        _opened = true;

        _accountText ??= D.TextOn(D.FieldAccount, "", 0.7f);
        _passwordText ??= D.TextOn(D.FieldPassword, "", 0.7f);
        _loginText ??= D.TextOn(D.LoginButton, "Login", 0.6f, new Color("eeeade"));
        ShowFields();

        D.FieldAccount.Activated = () => Edit(true);
        D.FieldPassword.Activated = () => Edit(false);
        D.LoginButton.Activated = DoLogin;
        D.Shield.Activated = () => Client.Game.Exit();
        D.Credits.Activated = ShowCredits;

        _quitText ??= D.TextOn(D.Shield, "Quit", 0.3f, new Color("eeeade"));
        _creditsText ??= D.TextOn(D.Credits, "Credits", 0.45f, new Color("eeeade"));

        // Short: the three sit close together on the chest's front.
        string[] studNames = { "Autologin", "Save", "Music" };

        for (int i = 0; i < 3; i++)
        {
            int index = i;
            D.Studs[i].Activated = () => ToggleStud(index);
            _studText[i] ??= D.TextAbove(D.Studs[i], studNames[i], 1, gap: 0.02f);
        }

        RefreshStuds();
        BuildCards();
        Link();

        D.Focus.Set(_account.Length > 0 ? D.FieldPassword : D.FieldAccount);
    }

    public override void Exit()
    {
        CloseCredits();
        ClearCards();
        ShowProps(false);
    }

    /// <summary>Shows or hides the login step's own objects.</summary>
    private void ShowProps(bool on)
    {
        D.Scene.Plaque.Visible = on;
        D.FieldAccount.Visible = on;
        D.FieldPassword.Visible = on;
        D.LoginButton.Visible = on;
        D.Shield.Visible = on;
        D.Credits.Visible = on;

        foreach (Hotspot stud in D.Studs)
        {
            stud.Visible = on;
        }
    }

    private void ShowFields()
    {
        _accountText.Text = _account;
        _passwordText.Text = new string('*', _password.Length);
    }

    // --- the neighbour graph (authored) ----------------------------------------------

    private void Link()
    {
        Hotspot a = D.FieldAccount, p = D.FieldPassword, login = D.LoginButton, quit = D.Shield, credits = D.Credits;
        Hotspot[] studs = D.Studs;

        a.Up = null; a.Down = p; a.Left = quit; a.Right = credits;
        p.Up = a; p.Down = login; p.Left = quit; p.Right = credits;
        login.Up = p; login.Down = studs[1]; login.Left = quit; login.Right = credits;
        quit.Up = null; quit.Right = a; quit.Left = null; quit.Down = studs[0];
        credits.Up = null; credits.Left = a; credits.Right = null; credits.Down = studs[2];

        for (int i = 0; i < studs.Length; i++)
        {
            studs[i].Up = login;
            studs[i].Left = i > 0 ? studs[i - 1] : quit;
            studs[i].Right = i + 1 < studs.Length ? studs[i + 1] : credits;
            studs[i].Down = _cards.Count > 0 ? _cards[Math.Min(i * _cards.Count / studs.Length, _cards.Count - 1)] : null;
        }

        if (_cards.Count > 0)
        {
            PadFocus.LinkRow(_cards.ConvertAll(c => (IFocusable) c));

            foreach (Hotspot c in _cards)
            {
                c.Up = studs[1];
                c.Down = null;
            }

            _cards[0].Left = null;
            _cards[^1].Right = null;
        }
    }

    // --- commands ---------------------------------------------------------------------

    public override bool Command(PadCmd cmd)
    {
        if (_credits != null)
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

            case PadCmd.X:
                D.Focus.Set(D.FieldAccount);
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
        bool account = D.Focus.Current == D.FieldAccount, password = D.Focus.Current == D.FieldPassword;

        if ((account || password) && k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed)
        {
            Edit(account);
            D.Keyboard.Key(k);
            return true;
        }

        if ((account || password) && k.Keycode == Godot.Key.Backspace)
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
                D.Focus.Set(account ? D.FieldPassword : D.LoginButton);
                D.RefreshHints();
            },
            cancel: () => D.RefreshHints(),
            changed: text => SetField(account, text));
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

        ShowFields();
    }

    private void DoLogin()
    {
        if (Login == null || Login.CurrentLoginStep != Game.Scenes.LoginSteps.Main)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_account))
        {
            D.Focus.Set(D.FieldAccount);
            Edit(true);
            return;
        }

        GD.Print($"[GUO] pregame3d: login as \"{_account}\" to {Settings.GlobalSettings.IP}:{Settings.GlobalSettings.Port}");
        Login.Connect(_account, _password);
    }

    // --- studs: the classic gump's three boxes -----------------------------------------

    private void ToggleStud(int i)
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
        RefreshStuds();
    }

    private void RefreshStuds()
    {
        Settings s = Settings.GlobalSettings;
        PregameDiorama.SetStud(D.Studs[0], s.AutoLogin);
        PregameDiorama.SetStud(D.Studs[1], s.SaveAccount);
        PregameDiorama.SetStud(D.Studs[2], s.LoginMusic);
    }

    // --- saved accounts as cards ---------------------------------------------------------

    private void BuildCards()
    {
        ClearCards();
        IReadOnlyList<SavedAccount> accounts;

        try
        {
            Settings s = Settings.GlobalSettings;
            _server = ServerBook.Find(s.IP, s.Port);
            accounts = _server == null ? Array.Empty<SavedAccount>() : AccountBook.For(_server);
        }
        catch (Exception ex)
        {
            // The keystore (libsecret) may be missing, e.g. a Deck in Game Mode: no cards then.
            GD.PrintErr($"[GUO] pregame3d: saved accounts unavailable: {ex.Message}");
            return;
        }

        List<Placement> slots = D.Scene.Layout.CardSlots;

        for (int i = 0; i < accounts.Count && i < slots.Count; i++)
        {
            SavedAccount account = accounts[i];
            Node3D visual = D.Scene.Card();
            visual.Transform = slots[i].Transform * visual.Transform;
            D.Scene.Root.AddChild(visual);
            Hotspot card = Hotspot.Wrap(visual, "Card" + i, D.Scene.Root);
            card.Activated = () => PickAccount(account);
            D.TextOn(visual, account.Name, 0.35f);
            D.AddPickable(card);
            _cards.Add(card);
            _cardAccounts.Add(account);
        }
    }

    private void ClearCards()
    {
        foreach (Hotspot c in _cards)
        {
            D.RemovePickable(c);
            c.QueueFree();
        }

        _cards.Clear();
        _cardAccounts.Clear();
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
            D.Focus.Set(D.FieldPassword);
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
        if (_credits != null)
        {
            return;
        }

        _credits = Overlay.Card(Overlay.Stone);
        VBoxContainer col = Overlay.Column(6);
        col.AddChild(Overlay.Text("Credits", Input.Touch.UoTheme.Heading, 2));
        Label body = Overlay.Text(PregameDiorama.CreditsText, Input.Touch.UoTheme.Ink, wrap: true);
        body.CustomMinimumSize = new Vector2(380, 0);
        col.AddChild(body);
        _credits.AddChild(col);
        D.OverlayRoot.AddChild(_credits);
        _credits.SetAnchorsPreset(Control.LayoutPreset.Center);
        _credits.GrowHorizontal = Control.GrowDirection.Both;
        _credits.GrowVertical = Control.GrowDirection.Both;
        D.RefreshHints();
    }

    private void CloseCredits()
    {
        _credits?.QueueFree();
        _credits = null;
        D?.RefreshHints();
    }
}
