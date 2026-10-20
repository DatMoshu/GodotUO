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
    internal event Action<ClilocDocument> DocumentReopening;
    internal Button AddButton => _add;
    internal Button ReloadButton => _reload;
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
        _lang.AccessibilityName = "Cliloc file language";
        _lang.ItemSelected += _ => SwitchLanguage();
        bar.AddChild(_lang);
        _changedOnly = new CheckBox { Text = "Project changes", TooltipText = "Only added, edited or removed strings" };
        _changedOnly.Toggled += _ => Refresh(); bar.AddChild(_changedOnly);
        _add = AssetUi.Action("Add", "Add localized string to this language's working copy", AddString); bar.AddChild(_add);
        _reload = AssetUi.Action("Reload", "Reopen selected language file and saved document…", ReloadLanguage); bar.AddChild(_reload);
    }

    protected override void SearchUiReady()
    { _scope = AssetUi.Label("Exact file records; runtime fallback and Clilocs.txt are separate."); AddChild(_scope); }

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
            _files[lang] = file; _lang.AddItem(lang);
            if (lang == current)
            {
                _lang.Selected = _lang.ItemCount - 1;
            }
        }

        base.OnDataLoaded();
    }

    private void SwitchLanguage()
    {
        _lang.Disabled = Speech; _changedOnly.Disabled = Speech;
        _add.Disabled = Speech || _lang.ItemCount == 0; _reload.Disabled = _add.Disabled;
        _scope.Text = Speech ? "Speech keyword matching uses the installed speech table."
            : "Exact file records; runtime fallback and Clilocs.txt are separate.";
        Refresh(); Reinspect();
    }

    private string Language => _lang.ItemCount == 0 ? "" : _lang.GetItemText(_lang.Selected);
    internal bool ChooseLanguage(string language)
    {
        for (int i = 0; i < _lang.ItemCount; i++) if (_lang.GetItemText(i) == language)
        { _lang.Selected = i; SwitchLanguage(); return true; }
        return false;
    }
    private string DocumentKey => (Data.Assets?.Root ?? EditorData.ProjectRoot()) + "|" + Language;
    internal ClilocDocument WorkingDocument
    {
        get
        {
            if (Speech || Language.Length == 0) return null;
            string key = DocumentKey;
            if (_documents.TryGetValue(key, out var document)) return document;
            if (_errors.ContainsKey(key)) return null;
            try
            {
                document = ClilocDocument.Open(Data.Assets?.Root ?? EditorData.ProjectRoot(), Data.ClientData, Language, _files[Language]);
                _documents[key] = document; return document;
            }
            catch (Exception e) { _errors[key] = e.Message; _scope.Text = "Could not open language document: " + e.Message; return null; }
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
        var dialog = new ConfirmationDialog { Title = "Reopen language document?", DialogText = "Discard this language's unsaved changes and reload its source file plus saved project document?", OkButtonText = "Reopen language" };
        string key = DocumentKey;
        dialog.Confirmed += () =>
        {
            if (_documents.TryGetValue(key, out var document)) DocumentReopening?.Invoke(document);
            _documents.Remove(key); _errors.Remove(key); dialog.QueueFree(); SwitchLanguage();
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
            inspection.Title = "Localized string"; inspection.Origin = $"{lang.ToUpperInvariant()} · Project working document · native file unchanged";
            inspection.Strings = document; inspection.StringId = id;
            inspection.StringsChanged = () => { if (IsVisibleInTree() && WorkingDocument == document) OnAssetsChanged(); };
        }
        return inspection;
    }

    private static string EscapeBbcode(string s) => s.Replace("[", "[lb]");
}
#endif
