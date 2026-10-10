#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin tab's Settings (sprint "Admin tab", AD4): a form over the picked server's configuration files, with plain
/// labels from the backend's schema (Schemas/&lt;backend&gt;.settings.json, <see cref="ServerSettings"/>), checks on
/// every changed value, the list of what changed, and Save and restart. The files are written while the server is
/// stopped, inside the run bar's restart, because ModernUO writes some of them from memory while it runs; the files
/// they replace are kept under Configuration/GUO-previous with secrets masked. A secret (a mail password, a webhook)
/// is never shown: it is typed into a masked box and kept in the workspace's secrets file and the server's own file.
/// Before the restart the change is recorded in the server's audit log (admin_settings "changed"); after it the form
/// asks the running server for the values it now holds (admin_settings "get").
/// </summary>
[Tool]
public partial class SettingsPanel : VBoxContainer
{
    private const string SchemaDir = "res://addons/guo_editor/Admin/Schemas/";

    private Label _status;
    private LineEdit _filter;
    private Button _reload, _save, _showOther;
    private VBoxContainer _form;
    private RichTextLabel _changes;
    private readonly Dictionary<string, Control> _editors = new();
    private readonly Dictionary<string, Label> _errors = new();
    private readonly Dictionary<string, Control> _rows = new();
    private readonly List<(Control Heading, string Group)> _headings = new();
    private readonly List<(Control Editor, Label Cover)> _masked = new();
    private bool _otherShown;
    private int _req;
    private List<SettingsChange> _pending;

    /// <summary>The Admin tab: the run bar's server, the admin channel, Restart.</summary>
    public AdminView Admin { get; set; }

    /// <summary>Sends a message on the admin channel; false when there is none.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    public event Action<string> Logged;

    /// <summary>The settings loaded from the picked server's folder, or null.</summary>
    internal ServerSettings Settings { get; private set; }

    /// <summary>Why there is no form, in plain words, or null.</summary>
    public string Problem { get; private set; }

    /// <summary>The last admin_settings reply.</summary>
    public JsonNode LastReply { get; private set; }

    public int Replies { get; private set; }

    /// <summary>The last "get" reply: what the running server holds.</summary>
    public JsonNode LastLive { get; private set; }

    /// <summary>After a save and restart: whether the running server reports every written setting (null before).</summary>
    public bool? LiveMatches { get; private set; }

    /// <summary>What the last save wrote, or null.</summary>
    internal SettingsWriteResult LastWrite { get; private set; }

    /// <summary>Why the last save failed, or null.</summary>
    public string LastWriteError { get; private set; }

    /// <summary>True from Save and restart until the restarted server's values are checked.</summary>
    public bool SavePending { get; private set; }

    public string ChangesText => _changes?.GetParsedText() ?? "";

    public string StatusText => _status?.Text ?? "";

    public SettingsPanel()
    {
        Name = "Settings";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_form != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _filter = new LineEdit { PlaceholderText = "Find a setting", CustomMinimumSize = new Vector2(Px(180), 0), ClearButtonEnabled = true,
            TooltipText = "Shows only the settings whose name or help has these words." };
        _filter.TextChanged += _ => ApplyFilter();
        bar.AddChild(_filter);
        _showOther = new Button { Text = "Show other settings", ToggleMode = true,
            TooltipText = "Every other line of the server's main file, by its own name. They have no plain label yet." };
        _showOther.Toggled += on => { _otherShown = on; ApplyFilter(); };
        bar.AddChild(_showOther);
        _reload = new Button { Text = "Reload", TooltipText = "Reads the server's files again and forgets changes not saved yet." };
        _reload.Pressed += () => Reload();
        bar.AddChild(_reload);
        _save = new Button { Text = "Save and restart", TooltipText = "Writes the changes, keeping the previous files, and restarts the server so it reads them." };
        _save.Pressed += ConfirmSave;
        bar.AddChild(_save);
        _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        bar.AddChild(_status);

        var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        split.AddChild(scroll);
        _form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_form);
        var bottom = new VBoxContainer { CustomMinimumSize = new Vector2(0, Px(110)) };
        split.AddChild(bottom);
        bottom.AddChild(new Label { Text = "Changes not saved yet" });
        _changes = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        bottom.AddChild(_changes);
        UpdateView();
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    private ServerProfile Server => Admin?.Run?.SelectedServer;

    /// <summary>Reads the picked server's files into the form. False, with the reason in <see cref="Problem"/>, when it cannot.</summary>
    public bool Reload()
    {
        if (_form == null)
        {
            _Ready();
        }

        Settings = null;
        Problem = null;
        ServerProfile s = Server;
        string backend = s == null ? null : SettingsSchema.BackendFor(s.Backend, s.ServerDirectory);
        if (s == null)
        {
            Problem = "No server is picked in the run bar.";
        }
        else if (string.IsNullOrEmpty(s.ServerDirectory) || !Directory.Exists(s.ServerDirectory))
        {
            Problem = $"\"{s.Name}\" names no server folder on this computer, so its settings cannot be read. A remote server's settings come later (AD7).";
        }
        else if (backend == null)
        {
            Problem = $"There is no settings form for \"{s.Name}\" yet: it is not a ModernUO server folder (no Configuration\\modernuo.json).";
        }
        else
        {
            try
            {
                string schemaPath = SchemaDir + SettingsSchema.FileName(backend);
                using var file = Godot.FileAccess.Open(schemaPath, Godot.FileAccess.ModeFlags.Read)
                    ?? throw new FileNotFoundException("the settings schema is missing", schemaPath);
                SettingsSchema schema = SettingsSchema.Parse(file.GetAsText());
                Settings = ServerSettings.Load(schema, s.ServerDirectory, key => ShardSecrets.ReadFile(key));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Problem = $"The settings of \"{s.Name}\" could not be read: {e.Message}";
            }
        }

        Build();
        if (Problem != null)
        {
            Log($"[color=orange]settings: {Problem}[/color]");
        }
        else
        {
            Log($"settings: read {Settings.Fields.Count()} settings of {s.Name} ({Settings.Schema.Title})"
                + (Settings.MissingFiles.Count > 0 ? $"; not there yet: {string.Join(", ", Settings.MissingFiles)}" : ""));
        }

        return Problem == null;
    }

    // The form: one heading per group, one row per setting (label, editor, its problem), other settings hidden at first.
    private void Build()
    {
        foreach (Node c in _form.GetChildren())
        {
            _form.RemoveChild(c);
            c.QueueFree();
        }

        _editors.Clear();
        _errors.Clear();
        _rows.Clear();
        _headings.Clear();
        if (Settings == null)
        {
            _form.AddChild(new Label { Text = Problem ?? "", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            UpdateView();
            return;
        }

        foreach (SettingsGroup g in Settings.Groups)
        {
            var heading = new VBoxContainer();
            var title = new Label { Text = g.Name };
            title.AddThemeFontSizeOverride("font_size", Px(15));
            heading.AddChild(title);
            if (g.Help.Length > 0)
            {
                heading.AddChild(new Label { Text = g.Help, Modulate = new Color(1, 1, 1, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart });
            }

            _form.AddChild(heading);
            _headings.Add((heading, g.Name));
            foreach (SettingsField f in g.Fields)
            {
                _form.AddChild(Row(f));
            }
        }

        ApplyFilter();
        UpdateView();
    }

    private Control Row(SettingsField f)
    {
        var row = new HBoxContainer();
        string tip = (f.Help.Length > 0 ? f.Help + "\n" : "") + $"{Settings.Schema.Files[f.File]}: {f.Path.Replace('/', ' ')}"
            + (Settings.Present(f) ? "" : "\nNot in the file: the server uses its built-in value.");
        var label = new Label { Text = f.Label + (Settings.Present(f) || f.IsSecret ? "" : " (not set)"), TooltipText = tip, MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(Px(260), 0), ClipText = true };
        row.AddChild(label);
        Control editor = Editor(f);
        editor.TooltipText = tip;
        editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(editor);
        var error = new Label { Modulate = new Color(1f, 0.6f, 0.3f), CustomMinimumSize = new Vector2(Px(200), 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        row.AddChild(error);
        _editors[f.Id] = editor;
        _errors[f.Id] = error;
        _rows[f.Id] = row;
        return row;
    }

    private Control Editor(SettingsField f)
    {
        string v = Settings.Get(f) ?? "";
        switch (f.Type)
        {
            case "bool":
            {
                var box = new CheckBox { ButtonPressed = v == "True", Text = v == "True" ? "on" : "off" };
                box.Toggled += on =>
                {
                    box.Text = on ? "on" : "off";
                    Changed(f, on ? "True" : "False");
                };
                return box;
            }
            case "enum":
            case "expansion":
            {
                var pick = new OptionButton();
                for (int i = 0; i < f.Options.Length; i++)
                {
                    pick.AddItem(f.OptionLabel(f.Options[i]), i);
                }

                int at = Array.IndexOf(f.Options, v);
                pick.Selected = at;
                pick.ItemSelected += i => Changed(f, f.Options[(int)i]);
                return pick;
            }
            case "secret":
            {
                var box = new HBoxContainer();
                var state = new Label { Text = SecretState(f), CustomMinimumSize = new Vector2(Px(150), 0) };
                box.AddChild(state);
                var edit = new LineEdit { Secret = true, PlaceholderText = "type a new one", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                edit.TextChanged += t =>
                {
                    Settings.SetSecret(f, t.Length == 0 ? null : t);
                    state.Text = SecretState(f);
                    Refresh();
                };
                box.AddChild(edit);
                var clear = new Button { Text = "Clear", TooltipText = "Removes it from the server and from your secrets file when you save." };
                clear.Pressed += () =>
                {
                    edit.Text = "";
                    Settings.SetSecret(f, "");
                    state.Text = SecretState(f);
                    Refresh();
                };
                box.AddChild(clear);
                return box;
            }
            default:
                if (f.IsList)
                {
                    var text = new TextEdit { Text = v, CustomMinimumSize = new Vector2(0, Px(24) * Math.Clamp(v.Split('\n').Length + 1, 2, 5)) };
                    text.TextChanged += () => Changed(f, text.Text);
                    return text;
                }

                var line = new LineEdit { Text = v, PlaceholderText = Settings.Present(f) ? "" : "(the server's own value)" };
                line.TextChanged += t => Changed(f, t);
                return line;
        }
    }

    /// <summary>
    /// For a still of the form: true covers every value that names a folder on this computer (the UO data folders, or
    /// any setting holding a path), false shows them again. Returns how many are covered. The files are not touched.
    /// </summary>
    public int MaskFolders(bool on)
    {
        foreach ((Control editor, Label cover) in _masked)
        {
            if (IsInstanceValid(editor))
            {
                editor.Visible = true;
            }

            if (IsInstanceValid(cover))
            {
                cover.QueueFree();
            }
        }

        _masked.Clear();
        if (!on || Settings == null)
        {
            return 0;
        }

        foreach (SettingsField f in Settings.Fields)
        {
            if (_editors.GetValueOrDefault(f.Id) is not { } editor || !(f.Type is "folders" or "folder" || LooksLikePath(Settings.Get(f))))
            {
                continue;
            }

            var cover = new Label { Text = "(a folder on this computer, hidden in stills)", Modulate = new Color(1, 1, 1, 0.6f), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            editor.Visible = false;
            editor.GetParent().AddChild(cover);
            editor.GetParent().MoveChild(cover, editor.GetIndex() + 1);
            _masked.Add((editor, cover));
        }

        return _masked.Count;
    }

    private static bool LooksLikePath(string v) =>
        !string.IsNullOrEmpty(v) && System.Text.RegularExpressions.Regex.IsMatch(v, @"(?m)^\s*([A-Za-z]:[\\/]|\\\\|/[^/\s])");

    private string SecretState(SettingsField f) =>
        Settings.SecretEdited(f) ? "will change" : Settings.SecretSet(f) ? Settings.SecretKept(f) ? "set (in your secrets file)" : "set" : "not set";

    private void Changed(SettingsField f, string value)
    {
        Settings.Set(f, value);
        Refresh();
    }

    /// <summary>Sets a field by its id as if typed (the scripted check). False when there is no such field.</summary>
    public bool SetValue(string id, string value)
    {
        SettingsField f = Settings?.Field(id);
        if (f == null)
        {
            return false;
        }

        if (f.IsSecret)
        {
            Settings.SetSecret(f, value);
        }
        else
        {
            Settings.Set(f, value);
            switch (_editors.GetValueOrDefault(f.Id))
            {
                case LineEdit l:
                    l.Text = value;
                    break;
                case TextEdit t:
                    t.Text = value;
                    break;
                case CheckBox c:
                    c.SetPressedNoSignal(value == "True");
                    c.Text = value == "True" ? "on" : "off";
                    break;
                case OptionButton o:
                    o.Selected = Array.IndexOf(f.Options, value);
                    break;
            }
        }

        Refresh();
        return true;
    }

    /// <summary>Whether every secret's box hides what is typed (the scripted check).</summary>
    public bool SecretsMasked() => Settings != null && Settings.Fields.Where(f => f.IsSecret).All(f =>
        _editors.GetValueOrDefault(f.Id) is HBoxContainer box && box.GetChildren().OfType<LineEdit>().Any() && box.GetChildren().OfType<LineEdit>().All(l => l.Secret));

    /// <summary>The problems with the form now, by field id.</summary>
    public Dictionary<string, string> Problems() => Settings?.Validate() ?? new Dictionary<string, string>();

    // Problems beside their rows, and the list of changes.
    private void Refresh()
    {
        if (Settings == null)
        {
            UpdateView();
            return;
        }

        Dictionary<string, string> errors = Settings.Validate();
        foreach (var (id, label) in _errors)
        {
            label.Text = errors.TryGetValue(id, out string why) ? why : "";
        }

        UpdateView();
    }

    private void UpdateView()
    {
        if (_changes == null)
        {
            return;
        }

        var text = new StringBuilder();
        List<SettingsChange> diff = Settings?.Diff() ?? new List<SettingsChange>();
        Dictionary<string, string> errors = Settings?.Validate() ?? new Dictionary<string, string>();
        foreach (SettingsChange c in diff)
        {
            text.Append(errors.TryGetValue(c.Field.Id, out string why)
                ? $"[color=orange]{Escape(c.Describe())}: {Escape(why)}[/color]\n"
                : Escape(c.Describe()) + "\n");
        }

        foreach (string w in Settings?.Warnings() ?? new List<string>())
        {
            text.Append($"[color=yellow]{Escape(w)}[/color]\n");
        }

        _changes.Text = diff.Count == 0 ? "Nothing changed." : text.ToString();
        string blocker = SaveBlocker();
        _save.Disabled = blocker != null;
        _save.TooltipText = blocker ?? "Writes the changes, keeping the previous files, and restarts the server so it reads them.";
        _save.Text = Admin != null && Admin.Connected ? "Save and restart" : "Save";
        _reload.Disabled = Server == null || SavePending;
        _status.Text = SavePending ? "saving and restarting..."
            : Settings == null ? Problem ?? ""
            : diff.Count == 0 ? $"{Server?.Name}: {Settings.Schema.Title} settings, as on disk"
            : $"{diff.Count} change{(diff.Count == 1 ? "" : "s")}" + (errors.Count > 0 ? $", {errors.Count} to fix" : "") + (blocker != null && errors.Count == 0 ? $": {blocker}" : "");
    }

    private static string Escape(string s) => s.Replace("[", "[lb]");

    private void ApplyFilter()
    {
        string words = _filter?.Text.Trim() ?? "";
        var shownGroups = new HashSet<string>();
        foreach (SettingsField f in Settings?.Fields ?? Enumerable.Empty<SettingsField>())
        {
            bool show = (!f.IsOther || _otherShown || words.Length > 0)
                && (words.Length == 0 || words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(w => (f.Label + " " + f.Help + " " + f.Path + " " + f.Group).Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (_rows.TryGetValue(f.Id, out Control row))
            {
                row.Visible = show;
            }

            if (show)
            {
                shownGroups.Add(f.Group);
            }
        }

        foreach (var (heading, group) in _headings)
        {
            heading.Visible = shownGroups.Contains(group);
        }
    }

    /// <summary>Why the form cannot be saved now, or null.</summary>
    public string SaveBlocker()
    {
        if (Settings == null)
        {
            return Problem ?? "Nothing is loaded.";
        }

        if (SavePending)
        {
            return "A save is under way.";
        }

        if (Settings.Diff().Count == 0)
        {
            return "Nothing changed.";
        }

        if (Settings.Validate().Count > 0)
        {
            return "Fix the settings marked in orange first.";
        }

        if (Admin != null && Admin.Connected)
        {
            // Running with its bridge: the save goes through the restart, which needs the run bar and the admin channel.
            return Admin.RestartBlocker() is { } why ? "Save and restart: " + why : null;
        }

        // Not connected: write only when nothing answers on the server's game port (it looks stopped).
        return Server != null && Listening(Server.Host, Server.Port)
            ? $"\"{Server.Name}\" seems to be running (its port {Server.Port} answers) without this tab's admin channel. Stop it first, or start it from the run bar and connect, so Save and restart can do both."
            : null;
    }

    private static bool Listening(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, port).Wait(300) && c.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or ArgumentException)
        {
            return false;
        }
    }

    private void ConfirmSave()
    {
        if (SaveBlocker() is { } why)
        {
            Log($"[color=orange]settings: {why}[/color]");
            return;
        }

        bool restart = Admin != null && Admin.Connected;
        var dialog = new ConfirmationDialog
        {
            Title = restart ? "Save and restart " + Server.Name : "Save the settings of " + Server.Name,
            DialogText = string.Join("\n", Settings.Diff().Select(c => c.Describe()))
                + string.Concat(Settings.Warnings().Select(w => "\n" + w))
                + (restart ? "\n\nThe world is saved, the server stops, the files are written (the previous ones kept) and it starts again.\nEveryone online is disconnected until it is back."
                    : "\n\nThe files are written now (the previous ones kept); the server reads them when it starts."),
            OkButtonText = restart ? "Save and restart" : "Save",
        };
        AddChild(dialog);
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            Save();
        };
        dialog.PopupCentered();
    }

    /// <summary>
    /// Saves the form (no dialog: the confirm and the scripted check call this). With the admin channel: records the
    /// change in the server's audit, then the tab's Restart saves the world, the run bar stops the server, the files
    /// are written and it starts again. Without it, on a stopped server: writes the files now.
    /// </summary>
    public bool Save()
    {
        if (SaveBlocker() is { } why)
        {
            Log($"[color=orange]settings: {why}[/color]");
            return false;
        }

        _pending = Settings.Diff();
        LastWrite = null;
        LastWriteError = null;
        LiveMatches = null;
        if (Admin == null || !Admin.Connected)
        {
            WriteNow();
            UpdateView();
            return LastWriteError == null;
        }

        SavePending = true;
        Log($"settings: {_pending.Count} change{(_pending.Count == 1 ? "" : "s")} to save; recording them in the server's audit log...");
        Send?.Invoke(new JsonObject { ["op"] = "admin_settings", ["action"] = "changed", ["changes"] = Settings.AuditChanges(), ["req"] = ++_req });
        UpdateView();
        return true;
    }

    // Runs with the server stopped (inside the run bar's restart) or on a stopped server. Never throws: the server
    // starts again either way, and the reason is in the log.
    private void WriteNow()
    {
        try
        {
            LastWrite = Settings.Write((key, value) => ShardSecrets.Write(key, value), DateTime.UtcNow);
            Log($"settings: wrote {string.Join(", ", LastWrite.Files)}; the previous files are in Configuration\\{ServerSettings.PreviousFolderName}\\{Path.GetFileName(LastWrite.PreviousFolder)}"
                + (LastWrite.SecretsWritten.Count > 0 ? $"; {LastWrite.SecretsWritten.Count} secret(s) kept in your secrets file" : ""));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            LastWriteError = e.Message;
            Log($"[color=orange]settings: not written: {e.Message}[/color]");
        }
    }

    /// <summary>The admin channel opened (again): read the files afresh, and after a save check what the server now runs with.</summary>
    public void OnAdminOpen()
    {
        if (!SavePending || LastWrite == null)
        {
            if (Settings == null || Settings.Diff().Count == 0)
            {
                Reload();
            }

            return;
        }

        RequestLive(_pending);
    }

    public void OnClosed() => UpdateView();

    /// <summary>The restart stopped before the server was back (the save failed, the run bar failed, or it timed out).</summary>
    public void OnRestartAbandoned()
    {
        if (!SavePending)
        {
            return;
        }

        SavePending = false;
        LiveMatches = false;
        Log(LastWrite == null
            ? "[color=orange]settings: nothing was written, because the restart did not get as far as stopping the server[/color]"
            : "[color=orange]settings: the files were written, but the server did not come back to confirm them; see the Logs dock[/color]");
        UpdateView();
    }

    /// <summary>Asks the running server for the values of these changes' modernuo.json settings.</summary>
    internal bool RequestLive(IEnumerable<SettingsChange> changes)
    {
        var keys = new JsonArray();
        foreach (SettingsChange c in changes ?? Enumerable.Empty<SettingsChange>())
        {
            if (c.Field.File == "modernuo" && c.Field.Path.StartsWith("settings/", StringComparison.Ordinal))
            {
                keys.Add(c.Field.Path["settings/".Length..]);
            }
        }

        return Send?.Invoke(new JsonObject { ["op"] = "admin_settings", ["action"] = "get", ["keys"] = keys, ["req"] = ++_req }) == true;
    }

    public void Handle(JsonNode msg)
    {
        LastReply = msg;
        Replies++;
        bool ok = (bool?)msg["ok"] == true;
        if (!ok)
        {
            Log($"[color=orange]settings: the server refused: {(string)msg["error"]}[/color]");
            SavePending = false;
            UpdateView();
            return;
        }

        if (msg["values"] is JsonObject values)
        {
            LastLive = msg;
            if (SavePending)
            {
                SavePending = false;
                var wrong = new List<string>();
                foreach (SettingsChange c in _pending ?? new List<SettingsChange>())
                {
                    if (c.Field.File == "modernuo" && c.Field.Path.StartsWith("settings/", StringComparison.Ordinal) && !c.Field.IsSecret)
                    {
                        string live = (string)values[c.Field.Path["settings/".Length..]];
                        if (!string.Equals(live ?? "", c.To ?? "", StringComparison.Ordinal))
                        {
                            wrong.Add($"{c.Field.Label} is {live ?? "(not set)"}, not {c.To}");
                        }
                    }
                }

                string expansion = (string)msg["expansion"];
                SettingsChange exp = _pending?.FirstOrDefault(c => c.Field.Type == "expansion");
                if (exp != null && expansion != exp.To)
                {
                    wrong.Add($"the expansion is {expansion}, not {exp.To}");
                }

                LiveMatches = wrong.Count == 0 && LastWriteError == null;
                Log(LiveMatches == true
                    ? $"settings: the server is back and runs with the new settings ({values.Count} checked, expansion {expansion})"
                    : $"[color=orange]settings: after the restart {(LastWriteError != null ? "the files were not written (" + LastWriteError + ")" : string.Join("; ", wrong))}[/color]");
                Reload();
            }
        }
        else if (msg["recorded"] != null && SavePending)
        {
            Log("settings: recorded in the audit log; restarting the server to write them...");
            if (!Admin.Restart(WriteNow))
            {
                SavePending = false;
                Log("[color=orange]settings: the restart did not start, so nothing was written[/color]");
            }
        }

        UpdateView();
    }

    private void Log(string line) => Logged?.Invoke(line);
}
#endif
