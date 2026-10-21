#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Assets;

/// <summary>
/// Cliloc &amp; speech (plan §4.6 panel 6): search the localized strings in
/// any language the install has, and see which speech keywords the client
/// would detect in a phrase (<see cref="SpeechesLoader.GetKeywords"/>, the
/// call the client makes before it sends speech).
/// </summary>
[Tool]
public partial class ClilocPanel : GridPanel
{
    private OptionButton _kind, _lang;
    private CheckBox _changedOnly;
    private Label _scope;
    private Button _add, _reload;
    private readonly Dictionary<string, ClilocDocument> _documents = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly Dictionary<string, string> _files = new();
    private readonly Dictionary<string, string> _activeCustomKeys = new();
    internal event Action<ClilocDocument> DocumentReopening;
    internal Func<ClilocDocument, bool> PreparingExchange;
    internal Func<IReadOnlyList<ItemNameDraft>> CaptureItemNames;
    internal Button ImportCsvButton { get; private set; }
    internal Button ExportCsvButton { get; private set; }
    internal Button OpenCustomButton { get; private set; }
    internal Button SyncButton { get; private set; }
    internal Button NativeStageButton { get; private set; }
    internal Button AddButton => _add;
    internal Button ReloadButton => _reload;
    internal string SourceStatus => _scope.Text;
    internal void SelectKind(bool speech) { _kind.Selected = speech ? 1 : 0; SwitchLanguage(); }
    private readonly Dictionary<int, SpeechEntry> _speech = new();

    public override string SmokeQuery => "backpack";

    public override bool SmokeNeedsImage => false;

    protected override int IconSize => 0;

    private bool Speech => _kind != null && _kind.Selected == 1;

    protected override string Placeholder => "cliloc number or text; in Speech, a phrase";

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _kind = new OptionButton();
        _kind.AddItem("Cliloc");
        _kind.AddItem("Speech");
        _kind.ItemSelected += _ => SwitchLanguage();
        bar.AddChild(_kind);

        _lang = new OptionButton();
        _lang.AccessibilityName = "Cliloc source language or custom slot";
        _lang.ItemSelected += _ => SwitchLanguage();
        bar.AddChild(_lang);
        _changedOnly = new CheckBox { Text = "Project changes", TooltipText = "Only added, edited or removed strings" };
        _changedOnly.Toggled += _ => Refresh(); bar.AddChild(_changedOnly);
        _add = AssetUi.Action("Add", "Add localized string to this language's working copy", AddString); bar.AddChild(_add);
        _reload = AssetUi.Action("Reload", "Reopen selected language file and saved document…", ReloadLanguage); bar.AddChild(_reload);
    }

    protected override void SearchUiReady()
    {
        var tools = new HBoxContainer(); AddChild(tools);
        OpenCustomButton = AssetUi.Action("Folder", "Choose a file for the selected custom Cliloc slot…", () => OpenCustomPicker()); tools.AddChild(OpenCustomButton);
        ImportCsvButton = AssetUi.Action("Load", "Import Cliloc CSV into this language's working document…", () => ClilocCsvDialog.Choose(this, true)); tools.AddChild(ImportCsvButton);
        ExportCsvButton = AssetUi.Action("Save", "Export Cliloc CSV from the working language…", () => ClilocCsvDialog.Choose(this, false)); tools.AddChild(ExportCsvButton);
        SyncButton = AssetUi.Action("Reload", "Preview item-name synchronization from tiledata into this Cliloc working document…", () => OpenTileSync()); tools.AddChild(SyncButton);
        NativeStageButton = AssetUi.Action("Save", "Export a verified native Cliloc stage…", () => OpenNativeStage()); tools.AddChild(NativeStageButton);
        _scope = AssetUi.Label("Exact file records; runtime fallback and Clilocs.txt are separate."); tools.AddChild(_scope);
    }
    internal void PrepareExchange(ClilocDocument document)
    {
        if (document == null || Speech || WorkingDocument != document) throw new IOException("Choose the target Cliloc source document before exchanging CSV.");
        if (PreparingExchange?.Invoke(document) != true) throw new IOException("Resolve invalid text drafts or failed recovery in this source document before exchanging CSV. Reopen discards failed recovery.");
    }
    internal ClilocStageDialog OpenNativeStage()
    {
        try { ClilocDocument document = WorkingDocument; PrepareExchange(document); return ClilocStageDialog.Open(this, document, Data); }
        catch (Exception e) { AssetActions.ReportEdit(e.Message, "Native Cliloc export", this); return null; }
    }
    internal void RequireNativeReview(ClilocDocument document, string token)
    {
        PrepareExchange(document);
        if (!Path.GetFullPath(Project).Equals(document.Project, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Path.GetFullPath(Data.ClientData).Equals(document.Install, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Cliloc project or installation changed. Reopen native export.");
        document.RequireNativeReview(token);
    }
    internal ClilocCsvDialog OpenImportReview(string path, ClilocCsvFormat format)
    {
        ClilocDocument document = WorkingDocument; PrepareExchange(document);
        return ClilocCsvDialog.Review(this, document, new ClilocCsvPlan(document, ClilocCsv.ReadFile(path), format), path);
    }
    internal ClilocTileSyncDialog OpenTileSync()
    {
        ClilocDocument document = WorkingDocument; PrepareExchange(document);
        TileNameSnapshot Capture() => TileNameSnapshot.Capture(Data.Files.GetUOFilePath("tiledata.mul"),
            Data.Files.Version >= GUO.Utility.ClientVersion.CV_7090, Project, Data.ClientData,
            CaptureItemNames?.Invoke() ?? Array.Empty<ItemNameDraft>());
        return ClilocTileSyncDialog.Open(this, document, new ClilocTileSyncPlan(document, Capture(), Capture));
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        _lang.Clear();
        _files.Clear();
        string current = EditorData.Setting("UO_LANGUAGE", "enu").ToLowerInvariant();
        foreach (string file in Directory.GetFiles(Data.ClientData, "Cliloc.*").OrderBy(f => f))
        {
            string lang = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (lang.Length != 3 || !lang.All(c => c is >= 'a' and <= 'z')) continue;
            _files[lang] = file; _lang.AddItem(lang); _lang.SetItemMetadata(_lang.ItemCount - 1, lang);
            if (lang == current)
            {
                _lang.Selected = _lang.ItemCount - 1;
            }
        }
        for (int slot = 1; slot <= 2; slot++)
        { _lang.AddItem($"Custom {slot}"); _lang.SetItemMetadata(_lang.ItemCount - 1, $"custom{slot}"); }
        RestoreCustomSources();
        base.OnDataLoaded();
        SwitchLanguage(IsVisibleInTree());
    }

    internal void RestoreCustomSources()
    {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        foreach (string slot in new[] { "custom1", "custom2" })
        {
            string file = settings.GetProjectMetadata("guo_cliloc", slot + "_source", "").AsString();
            if (string.IsNullOrEmpty(file)) _files.Remove(slot);
            else
            {
                _files[slot] = file;
                try { EditorPaths.ProtectSource(file); }
                catch (Exception e) { _errors[KeyFor(slot, file)] = e.Message; }
            }
        }
    }
    internal EditorFileDialog OpenCustomPicker()
    {
        string slot = Language;
        if (Speech || slot is not ("custom1" or "custom2")) throw new IOException("Select Custom 1 or Custom 2 before choosing a Cliloc source file.");
        return AssetActions.Pick(EditorFileDialog.FileModeEnum.OpenFile, "", path => OpenCustomFile(slot, path),
            new[] { "cliloc.* ; Cliloc language files", "* ; All files" }, this, $"Choose Cliloc file for Custom {slot[^1]}");
    }
    internal ClilocDocument OpenCustomFile(string slot, string path, bool remember = true)
    {
        if (Speech || slot is not ("custom1" or "custom2")) throw new IOException("Choose custom source slot 1 or 2 in Cliloc mode.");
        string file = EditorPaths.Real(path), context = KeyFor(slot, file);
        ClilocDocument candidate = ClilocDocument.Open(Project, Data.ClientData, slot, file);
        string key = context + "|" + candidate.SourceHash;
        ClilocDocument document = _documents.GetValueOrDefault(key, candidate);
        // Commit the assignment only after parsing and saved-document validation succeed.
        _documents[key] = document; _activeCustomKeys[context] = key; _errors.Remove(context); _errors.Remove(key); _files[slot] = file;
        if (remember) EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_cliloc", slot + "_source", file);
        ChooseLanguage(slot); return document;
    }

    private void SwitchLanguage(bool inspect = true)
    {
        _lang.Disabled = Speech; _changedOnly.Disabled = Speech;
        bool assigned = _files.TryGetValue(Language, out string source);
        bool custom = Language is "custom1" or "custom2";
        _add.Disabled = Speech || !assigned; _reload.Disabled = _add.Disabled;
        ImportCsvButton.Disabled = ExportCsvButton.Disabled = SyncButton.Disabled = NativeStageButton.Disabled = _add.Disabled;
        OpenCustomButton.Disabled = Speech || !custom;
        OpenCustomButton.TooltipText = custom ? "Choose a Cliloc source file for this custom slot; other source drafts are retained."
            : "Select Custom 1 or Custom 2 to open a Cliloc file outside the installed language list.";
        _scope.Text = Speech ? "Speech keyword matching uses the installed speech table."
            : custom ? assigned ? $"Custom {Language[^1]} · {Path.GetFileName(source)} · source file is read-only."
                : $"Custom {Language[^1]} has no file assigned. Use Choose source file to open one."
            : "Exact file records; runtime fallback and Clilocs.txt are separate.";
        _scope.TooltipText = _lang.TooltipText = source ?? "";
        if (inspect) { Refresh(); EnsureSourceSelection(); }
    }
    public override void OnAssetsChanged() { base.OnAssetsChanged(); EnsureSourceSelection(); }
    private void EnsureSourceSelection()
    {
        if (Speech) { Reinspect(); return; }
        ClilocDocument document = WorkingDocument;
        if (document != null)
        {
            if (Selected is int selected && (document.Get(selected) != null || document.Changed(selected))) { Reinspect(); return; }
            int? first = document.ReviewIds.Select(id => (int?)id).FirstOrDefault();
            if (first.HasValue) { Search(first.Value.ToString()); return; }
        }
        Raise(Inspection.Still("Cliloc", "Source document", null, document == null
            ? "Choose an available Cliloc source file. The browser status explains any source error."
            : "This source contains no strings. Add a string or import CSV into the working copy."));
    }

    private string Language => _lang.ItemCount == 0 || _lang.Selected < 0 ? "" : _lang.GetItemMetadata(_lang.Selected).AsString();
    internal bool ChooseLanguage(string language)
    {
        for (int i = 0; i < _lang.ItemCount; i++) if (_lang.GetItemMetadata(i).AsString() == language)
        { _lang.Selected = i; SwitchLanguage(); return true; }
        return false;
    }
    private string Project => Data.Assets?.Root ?? EditorData.ProjectRoot();
    private string KeyFor(string language, string source) => Project + "|" + language + "|" + (OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source);
    private string SourceContext => KeyFor(Language, _files.GetValueOrDefault(Language, ""));
    private string DocumentKey => _activeCustomKeys.GetValueOrDefault(SourceContext, SourceContext);
    internal ClilocDocument WorkingDocument
    {
        get
        {
            if (Speech || Language.Length == 0 || !_files.TryGetValue(Language, out string file)) return null;
            string key = DocumentKey;
            if (_documents.TryGetValue(key, out var document)) return document;
            if (_errors.TryGetValue(key, out string error)) { _scope.Text = "Could not open source document: " + error; return null; }
            try
            {
                document = ClilocDocument.Open(Project, Data.ClientData, Language, file);
                if (document.IsCustom) { string context = SourceContext; key = context + "|" + document.SourceHash; _activeCustomKeys[context] = key; }
                _documents[key] = document; return document;
            }
            catch (Exception e) { _errors[key] = e.Message; _scope.Text = "Could not open source document: " + e.Message; return null; }
        }
    }

    private void AddString()
    {
        ClilocDocument document = WorkingDocument; if (document == null) return;
        var dialog = new ConfirmationDialog { Title = "Add localized string", OkButtonText = "Add to working copy" };
        var fields = new VBoxContainer(); dialog.AddChild(fields); fields.AddChild(AssetUi.Label("Non-negative decimal ID; new records use flag 0."));
        int suggested = 3_100_000; while (document.Get(suggested) != null && suggested < int.MaxValue) suggested++;
        var id = new LineEdit { Text = suggested.ToString(), AccessibilityName = "New cliloc ID" }; fields.AddChild(id);
        var error = AssetUi.Label(""); fields.AddChild(error);
        dialog.Confirmed += () =>
        {
            if (!int.TryParse(id.Text, out int value) || value < 0) { error.Text = "Enter a non-negative whole decimal ID."; dialog.PopupCentered(); return; }
            if (document.Get(value) != null) { error.Text = "That ID already exists. Choose another ID or search for it."; dialog.PopupCentered(); return; }
            try { document.Edit(value, ""); _changedOnly.ButtonPressed = false; Search(value.ToString()); dialog.QueueFree(); }
            catch (Exception e) { error.Text = e.Message; dialog.PopupCentered(); }
        };
        dialog.Canceled += dialog.QueueFree; AddChild(dialog); dialog.PopupCentered();
    }

    private void ReloadLanguage()
    {
        var dialog = new ConfirmationDialog { Title = "Reopen Cliloc source document?", DialogText = "Discard this source document's unsaved changes and reload its source file plus saved project document?", OkButtonText = "Reopen document" };
        string key = DocumentKey, context = SourceContext;
        dialog.Confirmed += () =>
        {
            if (_documents.TryGetValue(key, out var document)) DocumentReopening?.Invoke(document);
            _documents.Remove(key); _errors.Remove(key); _errors.Remove(context); _activeCustomKeys.Remove(context); dialog.QueueFree(); SwitchLanguage();
        };
        dialog.Canceled += dialog.QueueFree; AddChild(dialog); dialog.PopupCentered();
    }

    protected override IEnumerable<int> Ids()
    {
        if (Speech)
        {
            _speech.Clear();
            string phrase = SearchBox.Text.Trim();
            if (phrase.Length > 0)
            {
                foreach (SpeechEntry e in Data.Files.Speeches.GetKeywords(phrase))
                {
                    _speech[e.KeywordID] = e;
                }
            }

            return _speech.Keys.OrderBy(k => k);
        }

        ClilocDocument document = WorkingDocument;
        return document == null ? Array.Empty<int>() : _changedOnly.ButtonPressed ? document.ChangedIds : document.ReviewIds;
    }

    private string Text(int id) =>
        Speech
            ? string.Join(" | ", _speech.TryGetValue(id, out SpeechEntry e) ? e.Keywords : Array.Empty<string>())
            : WorkingDocument?.Get(id)?.Text ?? "[removed] " + (WorkingDocument?.Original(id)?.Text ?? "");

    protected override string Caption(int id)
    {
        string t = Text(id).Replace('\n', ' ');
        return $"{id}  {(t.Length > 90 ? t[..90] + "..." : t)}";
    }

    protected override bool Matches(int id, string query) =>
        Speech || Text(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Inspection Describe(int id)
    {
        string lang = Language;
        string text = Speech
            ? $"[b]Speech keyword 0x{id:X4}[/b] ({id})\nmatches: {Text(id)}\nphrase: \"{SearchBox.Text.Trim()}\"\n"
            : $"[b]Cliloc {id}[/b] (0x{id:X}) [{lang}]\n{EscapeBbcode(Text(id))}\n";

        Inspection inspection = Inspection.Still("Cliloc", id.ToString(), null, text);
        if (!Speech && WorkingDocument is { } document)
        {
            inspection.Title = "Localized string"; inspection.Origin = $"{document.DisplayName} · Project working document · native file unchanged";
            inspection.Strings = document; inspection.StringId = id;
            inspection.StringsChanged = () => { if (IsVisibleInTree() && WorkingDocument == document) OnAssetsChanged(); };
        }
        return inspection;
    }

    private static string EscapeBbcode(string s) => s.Replace("[", "[lb]");
}
#endif
