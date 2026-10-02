// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Scripting;
using GUO.Game.Data;
using GUO.Store;

namespace GUO.Input.Touch.Modern;

/// <summary>PORT DEVIATION (GUO): opt-in, in-game CE subset editor on desktop and touch.</summary>
internal sealed partial class ModernScripts : ModernGump
{
    private static ModernScripts _instance;
    private CodeEdit _editor;
    private LineEdit _name;
    private OptionButton _library;
    private Label _status;
    private Button _run, _stop;
    private ItemList _scriptList, _suggestions;
    private VBoxContainer _sidebar, _editPane;
    private HBoxContainer _libraryRow;
    private Label _signature;
    private Label _help, _hint;
    private PanelContainer _starterPopup;
    private LineEdit _starterSearch;
    private VBoxContainer _starterRows;
    private CodeEdit _starterPreview;
    private Label _starterDescription;
    private Label _starterTitle, _starterNote;
    private StarterScript _selectedStarter;
    private StoreScript _selectedPackScript;
    private System.Collections.Generic.IReadOnlyList<StoreScript> _storeScripts = Array.Empty<StoreScript>();
    private Button _addStarter;
    private VBoxContainer _packActions;
    private Button _approvePack, _enablePack, _runPack;
    private ScriptPackReview _packReview;
    private readonly string[] _spells = SpellsMagery.GetAllSpells.Values.Select(s => s.Name.ToLowerInvariant()).ToArray();
    private ScriptCompletion _completion;
    private bool _compact;
    private string _savedText = "", _savedName = "", _confirm = "";
    private string _notice = "";
    private ScriptLibrary _files;
    protected override bool DirectMouseInput => true;
    protected override float MaxArtWidth => 960f;

    public ModernScripts(World world) : base(world) { }

    public static void Show(World world)
    {
        if (!world.InGame || !UoTheme.Ready) return;
        if (_instance == null || !GodotObject.IsInstanceValid(_instance) || _instance.World != world)
        {
            if (_instance != null && GodotObject.IsInstanceValid(_instance)) _instance.QueueFree();
            _instance = new ModernScripts(world);
            Client.Game.AddChild(_instance);
        }
        if (Current != _instance) Current?.Close();
        _instance.Open();
    }

    public static bool HandleShortcut(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return false;
        World world = Client.Game?.UO?.World;
        if (world == null || !world.InGame) return false;
        if (key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.F12)
        {
            world.StopScripts();
            return true;
        }
        if (key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.R)
        {
            Show(world);
            return true;
        }
        if (IsOpen && Current == _instance && key.Keycode == Key.Escape)
        {
            if (_instance._starterPopup?.Visible == true) _instance._starterPopup.Hide();
            else if (_instance._suggestions.Visible) _instance._suggestions.Hide();
            else if (_instance._editor.GetCodeCompletionOptions().Count > 0) _instance._editor.CancelCodeCompletion();
            else _instance.Close();
            return true;
        }
        return false;
    }

    public static void ShowStorePack(World world, string id, string version)
    {
        Show(world);
        if (_instance == null || !_instance.Shown) return;
        _instance.ShowStarters();
        _instance._starterSearch.Text = id;
        var script = _instance._storeScripts.FirstOrDefault(s => s.Pack.Id == id && s.Pack.Version == version);
        if (script != null) _instance.SelectPackScript(script);
    }

    protected override void Build(PanelContainer card)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        card.AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        var title = UoTheme.Label("Scripts", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        AddButton(header, "Starter scripts", ShowStarters);
        AddButton(header, "Close", Close);
        var help = _help = UoTheme.Label("Razor CE compatibility preview • Choose a starter or write your own. Ctrl+Space suggests commands and arguments.", UoTheme.Muted);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(help);

        var workspace = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        workspace.AddThemeConstantOverride("separation", 12);
        column.AddChild(workspace);
        _sidebar = new VBoxContainer { CustomMinimumSize = new Vector2(170, 0) };
        workspace.AddChild(_sidebar);
        _sidebar.AddChild(UoTheme.Label("My scripts", UoTheme.Heading));
        _scriptList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _scriptList.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        _scriptList.AddThemeColorOverride("font_color", UoTheme.Ink);
        _scriptList.ItemSelected += index => _library.Select((int)index);
        _scriptList.ItemActivated += _ => LoadScript();
        _sidebar.AddChild(_scriptList);
        AddButton(_sidebar, "Load selected", LoadScript);
        _editPane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        workspace.AddChild(_editPane);
        var libraryRow = _libraryRow = new HBoxContainer();
        _editPane.AddChild(libraryRow);
        _library = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        libraryRow.AddChild(_library);
        AddButton(libraryRow, "Load", LoadScript);
        AddButton(libraryRow, "New", () =>
        {
            if (!ConfirmDiscard("new")) return;
            _editor.Text = "";
            _name.Text = "untitled";
            _savedText = "";
            _savedName = "";
            _notice = "New script";
        });

        var nameRow = new HBoxContainer();
        _editPane.AddChild(nameRow);
        _name = new LineEdit { Text = "example", PlaceholderText = "Script name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 64 };
        nameRow.AddChild(_name);
        AddButton(nameRow, "Save", SaveScript);
        _editor = new CodeEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 80),
            GuttersDrawLineNumbers = true,
            CodeCompletionEnabled = true,
            CodeCompletionPrefixes = new Godot.Collections.Array<string> { "'", "\"" },
            IndentAutomatic = true,
            LineFolding = true,
            HighlightCurrentLine = true,
            Text = "// Runs inside GUO; no assistant required.\nsysmsg 'Hello from GUO'\npause 1000\nsysmsg 'Finished'\n"
        };
        _editor.AddThemeStyleboxOverride("normal", UoTheme.Frame(UoTheme.FieldFrame, 4));
        _editor.AddThemeStyleboxOverride("read_only", UoTheme.Frame(UoTheme.FieldFrame, 4));
        _editor.AddThemeColorOverride("font_color", UoTheme.Ink);
        _editor.AddThemeColorOverride("line_number_color", UoTheme.Muted);
        _editor.AddThemeColorOverride("caret_color", UoTheme.Ink);
        _editor.AddThemeColorOverride("selection_color", new Color(UoTheme.Gold, 0.4f));
        _editor.SyntaxHighlighter = Highlighting();
        _editPane.AddChild(_editor);
        _editor.CodeCompletionRequested += Complete;
        _editor.CaretChanged += UpdateSignature;
        _editor.TextChanged += () =>
        {
            _confirm = ""; _notice = "";
            UpdateSignature();
            if (!_compact && _editor.HasFocus() && _editor.Editable) _editor.RequestCodeCompletion();
        };
        var assistRow = new HBoxContainer();
        _editPane.AddChild(assistRow);
        AddButton(assistRow, "Suggest", () => { _editor.GrabFocus(); Complete(); });
        AddButton(assistRow, "Indent", () => _editor.InsertTextAtCaret("    "));
        _signature = UoTheme.Label("", UoTheme.Muted);
        _signature.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _signature.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        assistRow.AddChild(_signature);
        _suggestions = new ItemList { Visible = false, CustomMinimumSize = new Vector2(0, 76) };
        _suggestions.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        _suggestions.AddThemeColorOverride("font_color", UoTheme.Ink);
        _suggestions.ItemSelected += index => AcceptSuggestion((int)index);
        _editPane.AddChild(_suggestions);
        _name.TextChanged += _ => { _confirm = ""; _notice = ""; };
        _status = UoTheme.Label("Ready", UoTheme.Ink);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _editPane.AddChild(_status);
        var actions = new HBoxContainer();
        _editPane.AddChild(actions);
        _run = AddButton(actions, "Run", () => { _notice = ""; World.StopScripts(); World.Scripts.Start(_editor.Text); Refresh(); });
        _stop = AddButton(actions, "Stop", () => { _notice = ""; World.StopScripts(); Refresh(); });
        var hint = _hint = UoTheme.Label("Close keeps scripts running. Stop: Ctrl+Shift+F12. Nothing runs automatically.", UoTheme.Muted);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _editPane.AddChild(hint);
        BuildStarters(card);
        FileAction(() => { _files = new ScriptLibrary(ProfileManager.ProfilePath); ReloadLibrary(); });
    }

    private static Button AddButton(Node row, string text, Action action)
    {
        Button button = UoTheme.Button(text, 55);
        button.Pressed += action;
        row.AddChild(button);
        return button;
    }

    private bool Dirty => _editor.Text != _savedText || _name.Text != _savedName;

    private bool ConfirmDiscard(string action)
    {
        if (Dirty && _confirm != action)
        {
            _confirm = action;
            _notice = "Unsaved edits. Press the same button again to discard them.";
            return false;
        }
        _confirm = "";
        return true;
    }

    private void ReloadLibrary()
    {
        _library.Clear();
        _scriptList.Clear();
        foreach (string name in _files.Names()) { _library.AddItem(name); _scriptList.AddItem(name); }
        for (int i = 0; i < _library.ItemCount; i++)
            if (_library.GetItemText(i) == _name.Text) { _library.Select(i); _scriptList.Select(i); }
    }

    private void LoadScript()
    {
        if (_library.Selected < 0) return;
        string name = _library.GetItemText(_library.Selected);
        if (!ConfirmDiscard("load:" + name)) return;
        FileAction(() =>
        {
            string source = _files.Read(name);
            _name.Text = _savedName = name;
            _editor.Text = _savedText = source;
            _notice = "Loaded " + name;
        });
    }

    private void SaveScript()
    {
        FileAction(() =>
        {
            _files ??= new ScriptLibrary(ProfileManager.ProfilePath);
            string name = _name.Text;
            bool exists = Array.Exists(_files.Names(), n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (exists && !string.Equals(_savedName, name, StringComparison.OrdinalIgnoreCase) && _confirm != "save:" + name)
            {
                _confirm = "save:" + name;
                _notice = "That name exists. Press Save again to replace it.";
                return;
            }
            _files.Save(name, _editor.Text);
            _savedText = _editor.Text;
            _savedName = name;
            _confirm = "";
            ReloadLibrary();
            _notice = "Saved " + name;
        });
    }

    private void FileAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            _notice = "File error: " + ex.Message;
            if (_starterPopup?.Visible == true) _starterDescription.Text = _starterDescription.TooltipText = _notice;
        }
    }

    protected override void Refresh()
    {
        // Wrapped labels initially measure before the card receives its width.
        // Once laid out, discard that transient minimum-height expansion.
        Card.Size = Card.CustomMinimumSize;
        _compact = TouchInput.Enabled || Card.Size.X < 760;
        _sidebar.Visible = !_compact;
        _help.Visible = !_compact;
        _hint.Visible = !_compact;
        bool shortCard = Card.CustomMinimumSize.Y < 450;
        _starterTitle.AddThemeFontSizeOverride("font_size", UoTheme.FontSize * (shortCard ? 1 : 2));
        _starterPreview.CustomMinimumSize = new Vector2(0, shortCard ? 60 : 115);
        _starterNote.Visible = !shortCard;
        _starterDescription.AutowrapMode = shortCard ? TextServer.AutowrapMode.Off : TextServer.AutowrapMode.WordSmart;
        _starterDescription.ClipText = shortCard;
        _starterDescription.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _run.Disabled = World.Scripts.Running;
        _stop.Disabled = !World.Scripts.Running && !World.PackScripts.Running;
        _editor.Editable = !World.Scripts.Running;
        _status.Text = (Dirty ? "Unsaved • " : "") +
            (string.IsNullOrEmpty(_notice) ? World.PackScripts.Running ? World.PackScripts.Status : World.Scripts.Status + (World.Scripts.Running ? $" • Line {World.Scripts.Line}" : "") : _notice);
        RefreshPackActions();
    }

    private static CodeHighlighter Highlighting()
    {
        var highlighter = new CodeHighlighter
        {
            NumberColor = new Color("854914"), SymbolColor = new Color("704077"),
            FunctionColor = new Color("214f78"), MemberVariableColor = new Color("704077")
        };
        foreach (var command in ScriptCatalog.Commands) highlighter.AddKeywordColor(command.Name, new Color("214f78"));
        foreach (string keyword in ScriptCatalog.Keywords) highlighter.AddKeywordColor(keyword, new Color("704077"));
        highlighter.AddColorRegion("'", "'", new Color("246035"));
        highlighter.AddColorRegion("\"", "\"", new Color("246035"));
        highlighter.AddColorRegion("//", "", new Color("687060"), true);
        highlighter.AddColorRegion("#", "", new Color("687060"), true);
        return highlighter;
    }

    private void UpdateSignature()
    {
        if (_signature == null) return;
        var command = ScriptCatalog.Describe(_editor.GetLine(_editor.GetCaretLine()));
        _signature.Text = command == null ? "Choose a command with Suggest" : command.Signature;
        _signature.TooltipText = command?.Description ?? "";
    }

    private void Complete()
    {
        if (!_editor.Editable) return;
        _completion = ScriptCatalog.Complete(_editor.GetLine(_editor.GetCaretLine()), _editor.GetCaretColumn(), _spells);
        _suggestions.Clear();
        if (_compact)
        {
            foreach (string candidate in _completion.Candidates) _suggestions.AddItem(candidate);
            _suggestions.Visible = _completion.Candidates.Length > 0;
            return;
        }
        foreach (string candidate in _completion.Candidates)
            _editor.AddCodeCompletionOption(CodeEdit.CodeCompletionKind.PlainText, candidate, candidate, UoTheme.Ink);
        _editor.UpdateCodeCompletionOptions(true);
    }

    private void AcceptSuggestion(int index)
    {
        if (_completion == null || index < 0 || index >= _completion.Candidates.Length) return;
        int line = _editor.GetCaretLine();
        _editor.Select(line, _completion.Start, line, _editor.GetCaretColumn());
        _editor.InsertTextAtCaret(_completion.Candidates[index]);
        _suggestions.Hide();
        _editor.GrabFocus();
    }

    private void BuildStarters(PanelContainer card)
    {
        _starterPopup = new PanelContainer { Visible = false, ZIndex = 10 };
        card.AddChild(_starterPopup);
        var contents = new VBoxContainer();
        _starterPopup.AddChild(contents);
        var header = new HBoxContainer();
        contents.AddChild(header);
        var title = _starterTitle = UoTheme.Label("Starter scripts", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        AddButton(header, "Asset store", () => { Close(); StoreWindow.Open(); });
        AddButton(header, "Back", () => _starterPopup.Hide());
        _starterSearch = new LineEdit { PlaceholderText = "Search by name or category" };
        _starterSearch.TextChanged += _ => FilterStarters();
        contents.AddChild(_starterSearch);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 72), SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        contents.AddChild(scroll);
        _starterRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_starterRows);
        _starterDescription = UoTheme.Label("Choose a script to preview it.", UoTheme.Ink);
        _starterDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        contents.AddChild(_starterDescription);
        _starterPreview = new CodeEdit { Editable = false, CustomMinimumSize = new Vector2(0, 115), SyntaxHighlighter = Highlighting(), GuttersDrawLineNumbers = true };
        _starterPreview.AddThemeStyleboxOverride("read_only", UoTheme.Frame(UoTheme.FieldFrame, 4));
        _starterPreview.AddThemeColorOverride("font_color", UoTheme.Ink);
        contents.AddChild(_starterPreview);
        _addStarter = AddButton(contents, "Add to my scripts", AddStarter);
        _packActions = new VBoxContainer { Visible = false };
        contents.AddChild(_packActions);
        var approvalRow = new HFlowContainer(); _packActions.AddChild(approvalRow);
        _approvePack = AddButton(approvalRow, "Approve", () => PackAction(() => World.PackScripts.Approve(_packReview)));
        _enablePack = AddButton(approvalRow, "Enable", () => PackAction(() => World.PackScripts.Enable(_packReview)));
        _runPack = AddButton(approvalRow, "Run pack", () => PackAction(() => { World.Scripts.Stop(); World.PackScripts.Run(_packReview.Identity); }));
        var managementRow = approvalRow;
        AddButton(managementRow, "Disable", () => PackAction(() => World.PackScripts.Disable(_packReview.Identity)));
        AddButton(managementRow, "Revoke", () => PackAction(() => World.PackScripts.Revoke(_packReview)));
        AddButton(managementRow, "Rollback", () => PackAction(() => World.PackScripts.Rollback(_packReview.Identity)));
        var note = _starterNote = UoTheme.Label("Adds a personal copy. It will not run or replace your current edits.", UoTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        contents.AddChild(note);
    }

    private void ShowStarters()
    {
        _storeScripts = Array.Empty<StoreScript>();
        FileAction(() =>
        {
            using var client = StoreOptions.CreateClient(StoreAddress.Default);
            _storeScripts = StoreScripts.List(client);
        });
        _starterPopup.Show();
        _starterSearch.Text = "";
        FilterStarters();
        SelectStarter(ScriptCatalog.Starters[0]);
    }

    private void FilterStarters()
    {
        foreach (Node row in _starterRows.GetChildren()) { _starterRows.RemoveChild(row); row.QueueFree(); }
        string query = _starterSearch.Text.Trim();
        foreach (var starter in ScriptCatalog.Starters)
        {
            if (!(starter.Title + " " + starter.Category + " " + starter.Description).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var button = AddButton(_starterRows, starter.Title + "  /  " + starter.Category, () => SelectStarter(starter));
            button.Alignment = HorizontalAlignment.Left;
        }
        foreach (var script in _storeScripts)
        {
            if (!(script.Title + " " + script.Category + " " + script.Pack.Id + " " + script.Pack.Author).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var button = AddButton(_starterRows, script.Path + "  /  " + script.Category, () => SelectPackScript(script));
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            button.TooltipText = script.Path + " / " + script.Category;
        }
    }

    private void SelectStarter(StarterScript starter)
    {
        _packReview = null;
        _packActions.Hide();
        _selectedStarter = starter;
        _selectedPackScript = null;
        _addStarter.Disabled = false;
        _starterDescription.Text = starter.Description;
        _starterDescription.TooltipText = starter.Description;
        _starterPreview.Text = starter.Source;
    }

    private void SelectPackScript(StoreScript script)
    {
        _packReview = null;
        _packActions.Hide();
        _selectedStarter = null;
        _selectedPackScript = null;
        _addStarter.Disabled = true;
        try
        {
            using var client = StoreOptions.CreateClient(StoreAddress.Default);
            string source = StoreScripts.Read(client, script);
            // A separate runner validates without ticking or disturbing the active script.
            var check = new ScriptRunner(new ClientScriptHost(World));
            bool supported = check.Start(source);
            string compatibility = supported ? "Ready to try in GUO; server requirements still apply." : "Not yet runnable in GUO: " + check.Status;
            check.Stop();
            _starterPreview.Text = source;
            _starterDescription.Text = $"{script.Category} · {script.Pack.Author} · {script.Pack.Licence}. {compatibility}";
            _starterDescription.TooltipText = _starterDescription.Text;
            _selectedPackScript = script;
            _addStarter.Disabled = false;
            if (script.ComponentId != null)
            {
                _packReview = World.PackScripts.Review(script.Pack.Id, script.Pack.Version, script.ComponentId);
                _starterPreview.Text = "// Approval grants: " + string.Join(", ", _packReview.Capabilities) +
                    "\n// Session-only; Enable and Run are separate.\n" + source;
                _packActions.Show();
                RefreshPackActions();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            _starterPreview.Text = "";
            _starterDescription.Text = "Cannot read pack: " + ex.Message;
            _starterDescription.TooltipText = _starterDescription.Text;
        }
    }

    private void PackAction(Action action)
    {
        if (_packReview == null) return;
        FileAction(action);
        RefreshPackActions();
    }

    private void RefreshPackActions()
    {
        if (_packReview == null || _approvePack == null) return;
        bool approved = World.PackScripts.IsApproved(_packReview);
        _approvePack.Disabled = approved;
        _enablePack.Disabled = !approved;
        _runPack.Disabled = !approved || !World.PackScripts.IsEnabled(_packReview);
    }

    private void AddStarter()
    {
        if (_selectedStarter == null && _selectedPackScript == null) return;
        FileAction(() =>
        {
            _files ??= new ScriptLibrary(ProfileManager.ProfilePath);
            string name;
            if (_selectedPackScript != null)
            {
                using var client = StoreOptions.CreateClient(StoreAddress.Default);
                name = StoreScripts.Import(client, _selectedPackScript, _files);
            }
            else name = _files.AddStarter(_selectedStarter);
            ReloadLibrary();
            for (int i = 0; i < _library.ItemCount; i++)
                if (_library.GetItemText(i) == name) { _library.Select(i); _scriptList.Select(i); }
            _notice = "Added " + name + ". Choose Load to edit it.";
            _starterPopup.Hide();
        });
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (!World.InGame)
        {
            Close();
            if (_instance == this) _instance = null;
            QueueFree();
        }
    }
}
