#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;

/// <summary>
/// The Admin tab's Accounts (sprint "Admin tab", AD5): every account on the server with its access level, last login,
/// characters and whether it is banned; and New account, Set access, New password, Ban and Unban, all through the
/// editor bridge's admin channel (admin_accounts, admin_account; ADR-0035), which audits each one. A password is
/// generated here (16 letters and digits, the most the login screen's box holds) or typed into a masked box; it is
/// sent once, in the request's "password" field, which the server's audit masks, and it is never logged, never put in
/// the tab's log and never sent back. The account names of the shard's owner and its game master lane come from the
/// configuration: the server sets those accounts' passwords back to the secrets file's at every start, and the panel
/// says so.
/// </summary>
[Tool]
public partial class AccountsPanel : VBoxContainer
{
    /// <summary>The longest name and password the login screen's boxes take (LoginGump.cs, as upstream).</summary>
    public const int MaxLength = 16;

    /// <summary>The shortest password the server takes from the tab (AccountRules.cs).</summary>
    public const int MinPasswordLength = 8;

    /// <summary>ModernUO's access levels, lowest first.</summary>
    public static readonly string[] Levels = { "Player", "Counselor", "GameMaster", "Seer", "Administrator", "Developer", "Owner" };

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private LineEdit _filter;
    private Button _refresh, _create, _setAccess, _password, _ban;
    private OptionButton _access;
    private Label _status, _hint;
    private Tree _tree;
    private RichTextLabel _details;
    private readonly List<JsonObject> _rows = new();
    private int _req;
    private bool _open;

    /// <summary>Sends a message on the admin channel; false when there is none.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    /// <summary>The access level the server granted the tab, or null.</summary>
    public Func<string> Granted { get; set; }

    public event Action<string> Logged;

    /// <summary>The accounts of the last list, by name.</summary>
    public IReadOnlyList<JsonObject> Rows => _rows;

    /// <summary>How many lists arrived (the smoke check waits on it).</summary>
    public int Lists { get; private set; }

    /// <summary>How many accounts the server holds (more than <see cref="Rows"/> when the list was cut short).</summary>
    public int Total { get; private set; }

    /// <summary>The last admin_account reply.</summary>
    public JsonNode LastReply { get; private set; }

    /// <summary>How many admin_account replies arrived.</summary>
    public int Replies { get; private set; }

    /// <summary>The account picked in the list, or null.</summary>
    public string Selected { get; private set; }

    public string StatusText => _status?.Text ?? "";

    public string HintText => _hint?.Text ?? "";

    public string DetailsText => _details?.GetParsedText() ?? "";

    public AccountsPanel()
    {
        Name = "Accounts";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_tree != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _filter = new LineEdit { PlaceholderText = "Find an account or character", CustomMinimumSize = new Vector2(Px(200), 0), ClearButtonEnabled = true,
            TooltipText = "Shows only the accounts whose name, or one of whose characters' names, has this text." };
        _filter.TextChanged += _ => Fill(true);
        bar.AddChild(_filter);
        _refresh = new Button { Text = "Refresh", TooltipText = "Asks the server for its accounts again." };
        _refresh.Pressed += () => RequestList();
        bar.AddChild(_refresh);
        _create = new Button { Text = "New account...", TooltipText = "Makes an account, with a generated or typed password." };
        _create.Pressed += () => OpenPasswordDialog(null);
        bar.AddChild(_create);
        _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        bar.AddChild(_status);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _tree = new Tree
        {
            Columns = 5,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        string[] titles = { "Account", "Access", "Last login", "Characters", "" };
        for (int i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, titles[i]);
            _tree.SetColumnClipContent(i, true);
            _tree.SetColumnExpand(i, i is 0 or 3);
        }

        _tree.SetColumnCustomMinimumWidth(1, Px(100));
        _tree.SetColumnCustomMinimumWidth(2, Px(170));
        _tree.SetColumnCustomMinimumWidth(4, Px(70));
        _tree.ItemSelected += () => Select(_tree.GetSelected()?.GetMetadata(0).AsString());
        split.AddChild(_tree);

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(Px(260), 0) };
        split.AddChild(side);
        _details = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true, FitContent = false };
        side.AddChild(_details);
        var levelRow = new HBoxContainer();
        side.AddChild(levelRow);
        _access = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The access level to give the account." };
        foreach (string l in Levels)
        {
            _access.AddItem(l);
        }

        levelRow.AddChild(_access);
        _setAccess = new Button { Text = "Set access", TooltipText = "Gives the account, and every character on it, the level picked beside." };
        _setAccess.Pressed += () =>
        {
            if (Selected != null)
            {
                SetAccess(Selected, Levels[_access.Selected]);
            }
        };
        levelRow.AddChild(_setAccess);
        _password = new Button { Text = "New password...", TooltipText = "Gives the account a new password, generated or typed." };
        _password.Pressed += () => OpenPasswordDialog(Selected);
        side.AddChild(_password);
        _ban = new Button { Text = "Ban" };
        _ban.Pressed += ConfirmBan;
        side.AddChild(_ban);
        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.7f) };
        side.AddChild(_hint);
        UpdateView();
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    /// <summary>A password of letters and digits that fits the login screen's box.</summary>
    public static string GeneratePassword(int length = MaxLength)
    {
        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            text.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return text.ToString();
    }

    /// <summary>Why the tab will not send this password, in plain words, or null (the server checks it again).</summary>
    public static string PasswordProblem(string password, string confirm = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Type a password, or let the tab make one.";
        }

        if (password.Length < MinPasswordLength || password.Length > MaxLength)
        {
            return $"A password has {MinPasswordLength} to {MaxLength} characters: the login screen's box holds {MaxLength}.";
        }

        return confirm != null && confirm != password ? "The two passwords are not the same." : null;
    }

    /// <summary>True when the configuration names this account as the shard's owner or one of its game master lane.</summary>
    public static bool SetFromSecrets(string account)
    {
        if (string.IsNullOrEmpty(account))
        {
            return false;
        }

        string owner = EditorData.Setting("UO_SHARD_OWNER", "");
        string gms = EditorData.Setting("UO_SHARD_GM_ACCOUNTS", "");
        return string.Equals(owner, account, StringComparison.OrdinalIgnoreCase)
            || gms.Split(',', ';', ' ').Any(g => string.Equals(g.Trim(), account, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The admin channel opened: list the accounts.</summary>
    public void OnAdminOpen()
    {
        _open = true;
        RequestList();
    }

    public void OnClosed()
    {
        _open = false;
        UpdateView();
    }

    /// <summary>Asks the server for every account.</summary>
    public bool RequestList()
    {
        if (Send?.Invoke(new JsonObject { ["op"] = "admin_accounts", ["req"] = ++_req }) != true)
        {
            return false;
        }

        _status.Text = "asking the server for its accounts...";
        return true;
    }

    /// <summary>Makes an account (no dialog: the dialog and the smoke check call this).</summary>
    public bool Create(string account, string level, string password, bool generated) =>
        Act("create", account, new JsonObject { ["access"] = level, ["password"] = password },
            $"making account '{account}' ({level}) with a {(generated ? "generated" : "typed")} password");

    /// <summary>Gives an account, and its characters, an access level.</summary>
    public bool SetAccess(string account, string level) =>
        Act("access", account, new JsonObject { ["access"] = level }, $"giving account '{account}' the level {level}");

    /// <summary>Gives an account a new password.</summary>
    public bool ResetPassword(string account, string password, bool generated) =>
        Act("password", account, new JsonObject { ["password"] = password },
            $"giving account '{account}' a new {(generated ? "generated" : "typed")} password");

    /// <summary>Bans an account (its players are disconnected) or lifts the ban.</summary>
    public bool Ban(string account, bool ban) =>
        Act(ban ? "ban" : "unban", account, new JsonObject(), ban ? $"banning account '{account}'" : $"lifting the ban on account '{account}'");

    // One admin_account request. The log line is written by the caller's words: it never holds the password.
    private bool Act(string action, string account, JsonObject fields, string doing)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return false;
        }

        if (fields["password"] is JsonNode p && PasswordProblem((string)p) is { } problem)
        {
            Log($"[color=orange]accounts: {problem}[/color]");
            return false;
        }

        fields["op"] = "admin_account";
        fields["action"] = action;
        fields["account"] = account.Trim();
        fields["req"] = ++_req;
        if (Send?.Invoke(fields) != true)
        {
            Log("[color=orange]accounts need the admin channel (connect first)[/color]");
            return false;
        }

        Log($"accounts: {doing}...");
        _status.Text = doing + "...";
        return true;
    }

    public void Handle(JsonNode msg)
    {
        switch ((string)msg["op"])
        {
            case "admin_accounts":
                if ((bool?)msg["ok"] != true)
                {
                    _status.Text = "the server refused the list: " + (string)msg["error"];
                    Log($"[color=orange]accounts: {(string)msg["error"]}[/color]");
                    break;
                }

                _rows.Clear();
                _rows.AddRange((msg["accounts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => (JsonObject)r.DeepClone()));
                Total = (int?)msg["count"] ?? _rows.Count;
                Lists++;
                if (Lists == 1)
                {
                    Log($"accounts: the server holds {Total} account{(Total == 1 ? "" : "s")}");
                }

                Fill(true);
                break;
            case "admin_account":
            {
                LastReply = msg;
                Replies++;
                string action = (string)msg["action"], account = (string)msg["account"];
                if ((bool?)msg["ok"] != true)
                {
                    _status.Text = $"refused: {(string)msg["error"]}";
                    Log($"[color=orange]accounts: {action} '{account}' refused: {(string)msg["error"]}[/color]");
                    break;
                }

                if (msg["row"] is JsonObject row)
                {
                    int at = _rows.FindIndex(r => string.Equals((string)r["name"], (string)row["name"], StringComparison.OrdinalIgnoreCase));
                    if (at >= 0)
                    {
                        _rows[at] = (JsonObject)row.DeepClone();
                    }
                    else
                    {
                        _rows.Add((JsonObject)row.DeepClone());
                        _rows.Sort((a, b) => string.Compare((string)a["name"], (string)b["name"], StringComparison.OrdinalIgnoreCase));
                        Total++;
                    }

                    Selected = (string)row["name"];
                }

                string said = action switch
                {
                    "create" => $"made account '{account}' ({(string)msg["row"]?["access"]})",
                    "access" => $"account '{account}' is {(string)msg["row"]?["access"]} now, and {(int?)msg["characters_changed"] ?? 0} of its characters changed with it",
                    "password" => $"account '{account}' has its new password",
                    "ban" => $"account '{account}' is banned" + ((int?)msg["disconnected"] is > 0 and var n ? $"; {n} connection(s) closed" : ""),
                    "unban" => $"account '{account}' may log in again",
                    _ => $"{action} '{account}' done",
                };
                _status.Text = said;
                Log("accounts: " + said);
                Fill(false);
                break;
            }
        }
    }

    // The list, filtered; the selection kept. With status, the status line counts what is shown.
    private void Fill(bool status)
    {
        if (_tree == null)
        {
            return;
        }

        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        string find = _filter.Text.Trim();
        int shown = 0;
        foreach (JsonObject r in _rows)
        {
            string name = (string)r["name"];
            string[] characters = (r["characters"] as JsonArray ?? new JsonArray()).Select(c => (string)c?["name"]).ToArray();
            if (find.Length > 0 && !name.Contains(find, StringComparison.OrdinalIgnoreCase)
                && !characters.Any(c => c?.Contains(find, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            TreeItem it = _tree.CreateItem(root);
            it.SetText(0, name);
            it.SetMetadata(0, name);
            it.SetText(1, (string)r["access"]);
            it.SetText(2, LastLogin(r));
            it.SetText(3, characters.Length == 0 ? "-" : string.Join(", ", characters));
            bool banned = (bool?)r["banned"] == true, online = (bool?)r["online"] == true;
            it.SetText(4, banned ? "banned" : online ? "online" : "");
            if (banned)
            {
                it.SetCustomColor(4, new Color(1f, 0.45f, 0.35f));
            }
            else if (online)
            {
                it.SetCustomColor(4, new Color(0.45f, 0.9f, 0.45f));
            }

            if (string.Equals(name, Selected, StringComparison.OrdinalIgnoreCase))
            {
                it.Select(0);
            }

            shown++;
        }

        if (status && Lists > 0)
        {
            _status.Text = $"{shown} of {Total} account{(Total == 1 ? "" : "s")}" + (Total > _rows.Count ? $" (the server listed the first {_rows.Count})" : "")
                + (find.Length > 0 ? $" matching \"{find}\"" : "");
        }

        UpdateView();
    }

    private void Select(string account)
    {
        Selected = account;
        if (Row(account) is { } r)
        {
            _access.Selected = Math.Max(0, Array.IndexOf(Levels, (string)r["access"]));
        }

        UpdateView();
    }

    /// <summary>Picks an account in the list (the smoke check and the dialogs).</summary>
    public void SelectAccount(string account)
    {
        Select(Row(account) is { } r ? (string)r["name"] : null);
        Fill(false);
    }

    private JsonObject Row(string account) =>
        account == null ? null : _rows.FirstOrDefault(r => string.Equals((string)r["name"], account, StringComparison.OrdinalIgnoreCase));

    // ModernUO stamps a new account's last login with the time it was made: that is "never".
    private static string LastLogin(JsonObject r)
    {
        string last = When((string)r["last_login"]);
        return last != "-" && last == When((string)r["created"]) ? "never" : last;
    }

    private static string When(string utc) =>
        DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
            ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z"
            : "-";

    /// <summary>Why the account buttons are off for the selection, in plain words, or null when they are on.</summary>
    public string Blocker()
    {
        if (!_open || Granted?.Invoke() == null)
        {
            return "Accounts need the admin channel: connect first.";
        }

        if (Row(Selected) is not { } r)
        {
            return "Pick an account in the list to change it.";
        }

        string held = Granted();
        int heldAt = Array.IndexOf(Levels, held), at = Array.IndexOf(Levels, (string)r["access"]);
        return held != "Owner" && at >= heldAt
            ? $"'{(string)r["name"]}' is {(string)r["access"]}: this tab ({held}) changes only accounts below its own level."
            : null;
    }

    private void UpdateView()
    {
        if (_tree == null)
        {
            return;
        }

        bool admin = _open && Granted?.Invoke() != null;
        _refresh.Disabled = !admin;
        _create.Disabled = !admin;
        string blocker = Blocker();
        _setAccess.Disabled = _password.Disabled = _ban.Disabled = blocker != null;
        JsonObject r = Row(Selected);
        bool banned = (bool?)r?["banned"] == true;
        _ban.Text = banned ? "Unban" : "Ban";
        _ban.TooltipText = banned ? "Lets the account log in again." : "Stops the account logging in, and disconnects it if it is online.";
        _hint.Text = blocker ?? "Set access changes the account's characters too. Ban asks first.";
        if (!admin && _status.Text.Length == 0)
        {
            _status.Text = "not connected";
        }

        _details.Text = r == null ? (admin ? "Pick an account to see it here." : "") : Describe(r);
    }

    // One account in plain words.
    private static string Describe(JsonObject r)
    {
        string name = (string)r["name"];
        var t = new StringBuilder();
        t.Append($"[b]{name}[/b] is {(string)r["access"]}.\n");
        string last = LastLogin(r);
        t.Append($"Made {When((string)r["created"])}, " + (last == "never" ? "never logged in.\n" : $"last logged in {last}.\n"));
        var characters = (r["characters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        t.Append(characters.Count == 0 ? "It has no characters.\n"
            : $"Characters: {string.Join(", ", characters.Select(c => (string)c["name"] + ((bool?)c["online"] == true ? " (online)" : "")))}.\n");
        if ((bool?)r["banned"] == true)
        {
            t.Append("[color=orange]It is banned: it cannot log in.[/color]\n");
        }

        if ((bool?)r["protected"] == true)
        {
            t.Append("It is a protected account: the server makes it Owner again whenever it logs in with its password.\n");
        }

        if (SetFromSecrets(name))
        {
            t.Append("Its password is in your secrets file: the server sets it back to that one at every start.\n");
        }

        return t.ToString();
    }

    private void ConfirmBan()
    {
        if (Row(Selected) is not { } r || Blocker() != null)
        {
            return;
        }

        bool banned = (bool?)r["banned"] == true;
        if (banned)
        {
            Ban((string)r["name"], false);
            return;
        }

        var dialog = new ConfirmationDialog
        {
            Title = "Ban " + (string)r["name"],
            DialogText = $"Ban the account '{(string)r["name"]}'?\nIt cannot log in until the ban is lifted, and if it is online now it is disconnected.",
            OkButtonText = "Ban",
        };
        AddChild(dialog);
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            Ban((string)r["name"], true);
        };
        dialog.PopupCentered();
    }

    // New account (account null) or New password: a generated password, shown masked with Show and Copy, or a typed one.
    private void OpenPasswordDialog(string account)
    {
        bool create = account == null;
        var dialog = new ConfirmationDialog { Title = create ? "New account" : "New password for " + account, OkButtonText = create ? "Make account" : "Set password" };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(Px(380), 0) };
        dialog.AddChild(box);
        LineEdit name = null;
        OptionButton level = null;
        if (create)
        {
            box.AddChild(new Label { Text = $"Account name (up to {MaxLength} characters)" });
            name = new LineEdit { MaxLength = MaxLength };
            box.AddChild(name);
            box.AddChild(new Label { Text = "Access level" });
            level = new OptionButton();
            foreach (string l in Levels)
            {
                level.AddItem(l);
            }

            box.AddChild(level);
        }
        else if (SetFromSecrets(account))
        {
            box.AddChild(new Label
            {
                Text = "This account's password is in your secrets file: the server sets it back to that one at its next start.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1f, 0.75f, 0.4f),
            });
        }

        var generate = new CheckBox { Text = "Make a password for me", ButtonPressed = true };
        box.AddChild(generate);
        var generatedRow = new HBoxContainer();
        box.AddChild(generatedRow);
        var generated = new LineEdit { Text = GeneratePassword(), Secret = true, Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        generatedRow.AddChild(generated);
        var show = new CheckBox { Text = "Show" };
        show.Toggled += on => generated.Secret = !on;
        generatedRow.AddChild(show);
        var copy = new Button { Text = "Copy", TooltipText = "Copies the password, to hand it to the player." };
        copy.Pressed += () => DisplayServer.ClipboardSet(generated.Text);
        generatedRow.AddChild(copy);
        var again = new Button { Text = "Another", TooltipText = "Makes a different one." };
        again.Pressed += () => generated.Text = GeneratePassword();
        generatedRow.AddChild(again);
        var typed = new LineEdit { Secret = true, MaxLength = MaxLength, PlaceholderText = $"password, {MinPasswordLength} to {MaxLength} characters", Visible = false };
        var confirm = new LineEdit { Secret = true, MaxLength = MaxLength, PlaceholderText = "the same again", Visible = false };
        box.AddChild(typed);
        box.AddChild(confirm);
        var problem = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1f, 0.6f, 0.4f) };
        box.AddChild(problem);
        box.AddChild(new Label
        {
            Text = "The password is sent once to the server and kept nowhere else: copy it now to hand it on.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.7f),
        });

        void Check()
        {
            string why = create && string.IsNullOrWhiteSpace(name.Text) ? "Name the account."
                : generate.ButtonPressed ? null : PasswordProblem(typed.Text, confirm.Text);
            problem.Text = why ?? "";
            dialog.GetOkButton().Disabled = why != null;
        }

        generate.Toggled += on =>
        {
            generatedRow.Visible = on;
            typed.Visible = confirm.Visible = !on;
            Check();
        };
        typed.TextChanged += _ => Check();
        confirm.TextChanged += _ => Check();
        if (name != null)
        {
            name.TextChanged += _ => Check();
        }

        AddChild(dialog);
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            string password = generate.ButtonPressed ? generated.Text : typed.Text;
            if (create)
            {
                Create(name.Text.Trim(), Levels[level.Selected], password, generate.ButtonPressed);
            }
            else
            {
                ResetPassword(account, password, generate.ButtonPressed);
            }

            dialog.QueueFree();
        };
        Check();
        dialog.PopupCentered();
    }

    private void Log(string line) => Logged?.Invoke(line);
}
#endif
