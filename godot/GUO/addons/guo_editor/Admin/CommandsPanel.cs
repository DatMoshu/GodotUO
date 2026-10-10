#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin tab's Commands (sprint "Admin tab", AD3): a palette over the server's own commands. The list comes
/// from the server (admin_commands: name, access level, usage, description) and is searched as you type; the help
/// line shows the usage and description of the command being typed; Up and Down walk the history; and the output
/// the server sends back is shown under it. No character needs to be online: the bridge runs the command as a hidden
/// admin presence at the tab's level, or, with "Run as", as your own online staff character (so a command that asks
/// for a target can be answered in game). Commands on the dangerous list (<see cref="AdminCommandRules"/>: shutdown,
/// wipe, delete accounts, global decorate, mass moves) run only after their word is typed in a confirm box; the
/// server checks the word again. Every command is audited by the server (admin_command; ADR-0035).
/// </summary>
[Tool]
public partial class CommandsPanel : VBoxContainer
{
    /// <summary>How many lines the history keeps.</summary>
    public const int HistoryLength = 50;

    private LineEdit _line, _as, _filter;
    private Button _run, _refresh;
    private Label _status, _help;
    private Tree _tree;
    private RichTextLabel _output;
    private readonly List<JsonObject> _rows = new();
    private readonly List<string> _history = new();
    private int _historyAt = -1;
    private int _req;
    private bool _open;
    private ConfirmationDialog _confirm;

    /// <summary>Sends a message on the admin channel; false when there is none.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    public event Action<string> Logged;

    /// <summary>The server's commands of the last list.</summary>
    public IReadOnlyList<JsonObject> Rows => _rows;

    /// <summary>How many lists arrived (the smoke check waits on it).</summary>
    public int Lists { get; private set; }

    /// <summary>True when the server returns command output (MUO patch 0005).</summary>
    public bool OutputAvailable { get; private set; }

    /// <summary>The last admin_command reply.</summary>
    public JsonNode LastReply { get; private set; }

    /// <summary>How many admin_command replies arrived.</summary>
    public int Replies { get; private set; }

    /// <summary>The lines run, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>The dangerous command waiting on its typed word, or null.</summary>
    internal AdminCommandRules.Danger Pending { get; private set; }

    public string StatusText => _status?.Text ?? "";

    public string HelpText => _help?.Text ?? "";

    public string OutputText => _output?.GetParsedText() ?? "";

    /// <summary>The commands the search shows now, by name.</summary>
    public IEnumerable<string> Shown => Items().Select(i => i.GetMetadata(0).AsString());

    public CommandsPanel()
    {
        Name = "Commands";
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
        _line = new LineEdit
        {
            PlaceholderText = "[where", SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = AdminCommandRules.MaxLength,
            TooltipText = "A server command, as you would type it in game. Enter runs it; Up and Down walk the history.",
        };
        _line.TextChanged += _ => ShowHelp();
        _line.TextSubmitted += _ => RunTyped();
        _line.GuiInput += OnLineInput;
        bar.AddChild(_line);
        _run = new Button { Text = "Run", TooltipText = "Runs the command on the server; its answer shows below." };
        _run.Pressed += RunTyped;
        bar.AddChild(_run);
        bar.AddChild(new Label { Text = "Run as" });
        _as = new LineEdit
        {
            PlaceholderText = "(no character needed)", CustomMinimumSize = new Vector2(Px(170), 0), ClearButtonEnabled = true,
            TooltipText = "Left empty, the command runs as the tab's own hidden admin presence, with nobody logged in.\n"
                + "Name your online staff character to run it as them, for a command that asks for a target in game.",
        };
        bar.AddChild(_as);

        _help = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.8f) };
        AddChild(_help);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(Px(300), 0) };
        split.AddChild(left);
        var findRow = new HBoxContainer();
        left.AddChild(findRow);
        _filter = new LineEdit
        {
            PlaceholderText = "Search commands", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true,
            TooltipText = "Shows only the commands whose name, alias, usage or description has this text.",
        };
        _filter.TextChanged += _ => Fill();
        findRow.AddChild(_filter);
        _refresh = new Button { Text = "Refresh", TooltipText = "Asks the server for its commands again." };
        _refresh.Pressed += () => RequestList();
        findRow.AddChild(_refresh);
        _tree = new Tree
        {
            Columns = 3,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        string[] titles = { "Command", "Access", "" };
        for (int i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, titles[i]);
            _tree.SetColumnClipContent(i, true);
            _tree.SetColumnExpand(i, i == 0);
        }

        _tree.SetColumnCustomMinimumWidth(1, Px(100));
        _tree.SetColumnCustomMinimumWidth(2, Px(70));
        _tree.ItemSelected += () => Pick(_tree.GetSelected()?.GetMetadata(0).AsString());
        _tree.ItemActivated += () => _line.GrabFocus();
        left.AddChild(_tree);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(right);
        var outRow = new HBoxContainer();
        right.AddChild(outRow);
        outRow.AddChild(new Label { Text = "Output" });
        _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, HorizontalAlignment = HorizontalAlignment.Right };
        outRow.AddChild(_status);
        var clear = new Button { Text = "Clear", TooltipText = "Empties the output (the server's audit log keeps every command)." };
        clear.Pressed += () => _output.Clear();
        outRow.AddChild(clear);
        _output = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SelectionEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(_output);
        ShowHelp();
        UpdateView();
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

    /// <summary>The admin channel opened: list the commands.</summary>
    public void OnAdminOpen()
    {
        _open = true;
        RequestList();
        UpdateView();
    }

    public void OnClosed()
    {
        _open = false;
        CloseConfirm();
        UpdateView();
    }

    /// <summary>Asks the server for its commands.</summary>
    public bool RequestList()
    {
        if (Send?.Invoke(new JsonObject { ["op"] = "admin_commands", ["req"] = ++_req }) != true)
        {
            return false;
        }

        if (_status != null)
        {
            _status.Text = "asking the server for its commands...";
        }

        return true;
    }

    /// <summary>Puts text in the command line (the smoke check types this way).</summary>
    public void Type(string text)
    {
        _line.Text = text ?? "";
        _line.CaretColumn = _line.Text.Length;
        ShowHelp();
    }

    /// <summary>Sets the search box.</summary>
    public void Search(string text)
    {
        _filter.Text = text ?? "";
        Fill();
    }

    /// <summary>Sets "Run as" (empty: no character).</summary>
    public void RunAs(string character) => _as.Text = character ?? "";

    private void RunTyped() => Run(_line.Text);

    /// <summary>
    /// Runs a command line: at once, or, when it is on the dangerous list, once its word is typed in the confirm box
    /// (<paramref name="confirm"/> given skips the box: the smoke check). False when nothing was sent.
    /// </summary>
    public bool Run(string text, string confirm = null)
    {
        if (AdminCommandRules.Check(text) is { } problem)
        {
            Say(problem, warn: true);
            return false;
        }

        string line = AdminCommandRules.Normalise(text);
        if (AdminCommandRules.Classify(line) is { } danger && !AdminCommandRules.Confirms(danger, confirm))
        {
            if (confirm != null)
            {
                Say($"'{confirm}' is not the word for {AdminCommandRules.Name(line)}: type '{danger.Confirm}'", warn: true);
                return false;
            }

            OpenConfirm(line, danger);
            return false;
        }

        return SendLine(line, confirm);
    }

    private bool SendLine(string line, string confirm)
    {
        var msg = new JsonObject { ["op"] = "admin_command", ["text"] = line, ["req"] = ++_req };
        string who = _as.Text.Trim();
        if (who.Length > 0)
        {
            msg["as"] = who;
        }

        if (confirm != null)
        {
            msg["confirm"] = confirm;
        }

        if (Send?.Invoke(msg) != true)
        {
            Say("commands need the admin channel (connect first)", warn: true);
            return false;
        }

        Remember(line);
        _line.Text = "";
        ShowHelp();
        string shown = AdminCommandRules.ForLog(line);
        _output.AppendText($"\n[b]> {Escape(shown)}[/b]{(who.Length > 0 ? $"  [i](as {Escape(who)})[/i]" : "")}\n");
        _status.Text = $"running {shown}...";
        Logged?.Invoke($"commands: running {Escape(shown)}{(who.Length > 0 ? " as " + Escape(who) : "")}...");
        return true;
    }

    private void Remember(string line)
    {
        // A line holding a password is not kept for Up to bring back.
        string kept = AdminCommandRules.ForLog(line);
        if (kept != line)
        {
            return;
        }

        _history.Remove(line);
        _history.Add(line);
        if (_history.Count > HistoryLength)
        {
            _history.RemoveAt(0);
        }

        _historyAt = -1;
    }

    /// <summary>Walks the history: -1 an older line, +1 a newer one (past the newest, an empty line).</summary>
    public void Walk(int step)
    {
        if (_history.Count == 0)
        {
            return;
        }

        int at = _historyAt < 0 ? _history.Count : _historyAt;
        at = Math.Clamp(at + step, 0, _history.Count);
        _historyAt = at == _history.Count ? -1 : at;
        Type(_historyAt < 0 ? "" : _history[at]);
    }

    private void OnLineInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k && k.Keycode is Key.Up or Key.Down)
        {
            Walk(k.Keycode == Key.Up ? -1 : 1);
            _line.AcceptEvent();
        }
    }

    // The confirm box: the danger in plain words, and a box where the word is typed; OK only once it is.
    private void OpenConfirm(string line, AdminCommandRules.Danger danger)
    {
        CloseConfirm();
        Pending = danger;
        _confirm = new ConfirmationDialog { Title = $"Run {AdminCommandRules.ForLog(line)}?", OkButtonText = "Run it" };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(Px(420), 0) };
        _confirm.AddChild(box);
        box.AddChild(new Label
        {
            Text = $"{AdminCommandRules.ForLog(line)} is on the dangerous list ({danger.Kind}).\n{danger.Why}\n\nType  {danger.Confirm}  to run it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var word = new LineEdit { PlaceholderText = danger.Confirm };
        box.AddChild(word);
        Button ok = _confirm.GetOkButton();
        ok.Disabled = true;
        word.TextChanged += t => ok.Disabled = !AdminCommandRules.Confirms(danger, t);
        word.TextSubmitted += t =>
        {
            if (AdminCommandRules.Confirms(danger, t))
            {
                _confirm.Hide();
                _confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
            }
        };
        AddChild(_confirm);
        ConfirmationDialog dialog = _confirm;
        dialog.Canceled += () =>
        {
            Say($"{AdminCommandRules.Name(line)} not run");
            CloseConfirm();
        };
        dialog.Confirmed += () =>
        {
            string typed = word.Text;
            CloseConfirm();
            SendLine(line, typed);
        };
        dialog.PopupCentered();
        word.GrabFocus();
        Logged?.Invoke($"commands: {Escape(AdminCommandRules.Name(line))} is on the dangerous list ({danger.Kind}); waiting for its word");
    }

    /// <summary>Closes the confirm box without running (Cancel; the smoke check presses it this way).</summary>
    public void CancelConfirm()
    {
        if (Pending != null)
        {
            Say("not run");
        }

        CloseConfirm();
    }

    private void CloseConfirm()
    {
        Pending = null;
        if (_confirm != null && IsInstanceValid(_confirm))
        {
            _confirm.QueueFree();
        }

        _confirm = null;
    }

    public void Handle(JsonNode msg)
    {
        switch ((string)msg["op"])
        {
            case "admin_commands":
                if ((bool?)msg["ok"] != true)
                {
                    _status.Text = "the server refused the list: " + (string)msg["error"];
                    Logged?.Invoke($"[color=orange]commands: {Escape((string)msg["error"])}[/color]");
                    break;
                }

                _rows.Clear();
                _rows.AddRange((msg["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => (JsonObject)r.DeepClone()));
                OutputAvailable = (bool?)msg["output_available"] == true;
                Lists++;
                _status.Text = $"{_rows.Count} commands" + (OutputAvailable ? "" : "; this server does not return their output (it lacks MUO patch 0005)");
                Fill();
                ShowHelp();
                break;
            case "admin_command":
                LastReply = msg.DeepClone();
                Replies++;
                Answered(msg);
                break;
        }

        UpdateView();
    }

    private void Answered(JsonNode msg)
    {
        string text = (string)msg["text"] ?? "";
        if ((bool?)msg["ok"] != true)
        {
            _output.AppendText($"[color=orange]{Escape((string)msg["error"])}[/color]\n");
            _status.Text = "refused: " + (string)msg["error"];
            Logged?.Invoke($"[color=orange]commands: {Escape(text)} refused: {Escape((string)msg["error"])}[/color]");
            return;
        }

        var lines = (msg["output"] as JsonArray ?? new JsonArray()).Select(l => (string)l).ToList();
        foreach (string l in lines)
        {
            _output.AppendText(Escape(l) + "\n");
        }

        if (lines.Count == 0)
        {
            _output.AppendText((bool?)msg["output_available"] == false
                ? "[i](ran; this server does not return command output)[/i]\n"
                : "[i](ran; the server said nothing back. A command that opens a gump shows it only in a game client.)[/i]\n");
        }

        string who = (string)msg["as"];
        _status.Text = $"{text}: {lines.Count} line{(lines.Count == 1 ? "" : "s")}";
        Logged?.Invoke($"commands: {Escape(text)} ran{(who != null ? " as " + Escape(who) : "")}, {lines.Count} line{(lines.Count == 1 ? "" : "s")} back"
            + ((bool?)msg["needs_target"] == true ? " (it wanted a target)" : ""));
    }

    private JsonObject Row(string name) =>
        name == null ? null : _rows.FirstOrDefault(r => string.Equals((string)r["name"], name, StringComparison.OrdinalIgnoreCase));

    // A command by its name or one of its aliases.
    private JsonObject Find(string name) =>
        Row(name) ?? _rows.FirstOrDefault(r => (r["aliases"] as JsonArray ?? new JsonArray())
            .Any(a => string.Equals((string)a, name, StringComparison.OrdinalIgnoreCase)));

    private bool Matches(JsonObject r, string f) =>
        f.Length == 0
        || ((string)r["name"] ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
        || ((string)r["usage"] ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
        || ((string)r["description"] ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
        || (r["aliases"] as JsonArray ?? new JsonArray()).Any(a => ((string)a ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));

    private void Fill()
    {
        if (_tree == null)
        {
            return;
        }

        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        string f = _filter.Text.Trim().TrimStart('[');
        foreach (JsonObject r in _rows.Where(r => Matches(r, f)))
        {
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, (string)r["name"]);
            item.SetMetadata(0, (string)r["name"]);
            item.SetText(1, (string)r["access"]);
            if ((string)r["danger"] is { } kind)
            {
                item.SetText(2, "confirm");
                item.SetCustomColor(2, new Color(1f, 0.6f, 0.3f));
                item.SetTooltipText(2, $"On the dangerous list ({kind}): it runs only after its word is typed.");
            }

            item.SetTooltipText(0, $"{(string)r["usage"]}\n{(string)r["description"]}");
        }
    }

    private IEnumerable<TreeItem> Items()
    {
        TreeItem root = _tree?.GetRoot();
        for (TreeItem i = root?.GetFirstChild(); i != null; i = i.GetNext())
        {
            yield return i;
        }
    }

    // A command picked in the list goes in the line, ready for its arguments.
    private void Pick(string name)
    {
        if (name == null)
        {
            return;
        }

        Type(AdminCommandRules.Prefix + name + " ");
    }

    // The help line: the usage and description of the command being typed, and whether it is dangerous.
    private void ShowHelp()
    {
        if (_help == null)
        {
            return;
        }

        string name = AdminCommandRules.Name(_line.Text);
        if (name.Length == 0)
        {
            _help.Text = Lists == 0
                ? "Connect to list the server's commands."
                : "Type a command, or pick one from the list. Up and Down bring back the ones you ran.";
            return;
        }

        JsonObject r = Find(name);
        if (r == null)
        {
            var near = _rows.Select(x => (string)x["name"]).Where(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            _help.Text = near.Count > 0 ? "Commands starting so: " + string.Join(", ", near) : $"The server lists no command '{name}'.";
            return;
        }

        string help = $"{AdminCommandRules.Prefix}{(string)r["usage"]}  ({(string)r["access"]})  {(string)r["description"]}";
        if (AdminCommandRules.Classify(_line.Text) is { } d)
        {
            help += $"\nDangerous ({d.Kind}): it asks you to type '{d.Confirm}' first.";
        }

        _help.Text = help;
    }

    private void Say(string line, bool warn = false)
    {
        if (_status != null)
        {
            _status.Text = line;
        }

        Logged?.Invoke(warn ? $"[color=orange]commands: {Escape(line)}[/color]" : $"commands: {Escape(line)}");
    }

    private void UpdateView()
    {
        if (_run == null)
        {
            return;
        }

        _run.Disabled = !_open;
        _refresh.Disabled = !_open;
        _line.Editable = _open;
        if (!_open && Lists == 0)
        {
            _status.Text = "connect to run commands";
        }
    }
}
#endif
