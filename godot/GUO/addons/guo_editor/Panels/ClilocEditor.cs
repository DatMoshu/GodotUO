#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Manual localized-string editing, with per-record raw drafts and source-bound recovery.</summary>
[Tool]
public partial class ClilocEditor : VBoxContainer
{
    private readonly Dictionary<(ClilocDocument Document, int Id), string> _raw = new();
    private readonly HashSet<ClilocDocument> _documents = new();
    private readonly HashSet<string> _recoveryFailed = new();
    private readonly Dictionary<string, string> _recoveryErrors = new();
    private ClilocDocument _document;
    private int _id;
    private Action _changed;
    private TextEdit _text;
    private Label _source, _identity, _status, _state;
    private Button _save, _undo, _redo, _delete;
    private bool _refreshing;
    public event Action Changed;
    internal VBoxContainer Footer { get; private set; }
    internal TextEdit TextInput => _text;
    internal Button SaveButton => _save;
    internal ClilocDocument Document => _document;
    internal int Id => _id;
    public string Summary => _document == null ? "Select a localized string." : $"{_document.Language}: {_document.ChangeCount} project change(s) · {(_document.IsDirty || _raw.Keys.Any(key => key.Document == _document) ? "unsaved" : "saved")}";

    public override void _Ready()
    {
        if (_text != null) return;
        _identity = AssetUi.Label(""); AddChild(_identity);
        AddChild(new Label { Text = "Working text" });
        _text = new TextEdit { WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 150) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = "Localized string working text", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_text); _text.TextChanged += Capture; _text.FocusExited += () => CommitPending(); _text.GuiInput += Shortcut;
        _status = AssetUi.Label(""); AddChild(_status);
        var toggle = new Button { Text = "Source text", ToggleMode = true, Flat = true, Alignment = HorizontalAlignment.Left, Icon = AssetUi.Icon("GuiTreeArrowRight") }; AddChild(toggle);
        _source = AssetUi.Label(""); _source.Visible = false; AddChild(_source);
        toggle.Toggled += open => { _source.Visible = open; toggle.Icon = AssetUi.Icon(open ? "GuiTreeArrowDown" : "GuiTreeArrowRight"); };
        Footer = new VBoxContainer(); AddChild(Footer); _state = AssetUi.Label(""); Footer.AddChild(_state);
        var bar = new HBoxContainer(); Footer.AddChild(bar);
        _undo = AssetUi.Action("Undo", "Undo string document edit", Undo); bar.AddChild(_undo);
        _redo = AssetUi.Action("Redo", "Redo string document edit", () => { if (CommitPending()) { _document?.Redo(); Read(); Notify(); } }); bar.AddChild(_redo);
        bar.AddChild(AssetUi.Action("Reload", "Revert this string to its source value (undoable)", () => { if (_document == null) return; _raw.Remove((_document, _id)); _document.Revert(_id); Read(); Notify(); }));
        _delete = AssetUi.Action("Remove", "Remove this string from the working copy…", ConfirmDelete); bar.AddChild(_delete);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _save = AssetUi.Action("Save", "Save selected language's project document · Ctrl+S", () => Save()); _save.Text = "Save copy"; bar.AddChild(_save);
    }

    internal void Bind(ClilocDocument document, int id, Action changed)
    {
        if (_text == null) _Ready();
        CommitPending(); _document = document; _id = id; _changed = changed;
        if (_documents.Add(document)) RestoreDraft(document);
        Read();
    }
    private void Capture()
    {
        if (_refreshing || _document == null) return;
        if (_text.Text == (_document.Get(_id)?.Text ?? "")) _raw.Remove((_document, _id));
        else _raw[(_document, _id)] = _text.Text;
        UpdateState();
    }
    private static string Error(string text)
    {
        if (text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > 32767)
            return "Text must fit 32,767 UTF-8 bytes and contain no NUL.";
        return "";
    }
    internal bool CommitPending()
    {
        if (_document == null || !_raw.TryGetValue((_document, _id), out string text)) return true;
        string error = Error(text); if (error.Length > 0) { _status.Text = error; UpdateState(); return false; }
        try { _document.Edit(_id, text); _raw.Remove((_document, _id)); UpdateState(); Notify(); return true; }
        catch (Exception e) { _status.Text = _state.Text = e.Message; return false; }
    }
    internal bool Save()
    {
        if (_document == null || !PrepareExchange(_document)) return false;
        try { _document.Save(); StashDrafts(); Read(); _state.Text = "Saved project document. Native cliloc is unchanged."; Notify(); return true; }
        catch (Exception e) { _status.Text = _state.Text = e.Message; return false; }
    }
    internal bool PrepareExchange(ClilocDocument document)
    {
        if (_documents.Add(document)) RestoreDraft(document);
        if (_recoveryFailed.Contains(KeyOf(document))) return false;
        var fields = _raw.Where(field => field.Key.Document == document).ToArray();
        try
        {
            document.ApplyTextBatch(fields.ToDictionary(field => field.Key.Id, field => field.Value));
            foreach (var field in fields) _raw.Remove(field.Key);
            if (_document == document) { Read(); Notify(); }
            return true;
        }
        catch (Exception e) { if (_document == document) _status.Text = _state.Text = e.Message; return false; }
    }
    internal void Undo()
    {
        if (_document == null) return;
        if (_raw.TryGetValue((_document, _id), out string text) && Error(text).Length > 0)
            _raw.Remove((_document, _id));
        else if (CommitPending()) _document.Undo();
        Read(); Notify();
    }
    private void Read()
    {
        if (_document == null || _text == null) return;
        _refreshing = true;
        ClilocDocument.Entry current = _document.Get(_id), original = _document.Original(_id);
        string value = _raw.GetValueOrDefault((_document, _id), current?.Text ?? "");
        if (_text.Text != value) _text.Text = value;
        _text.Editable = current != null && !_recoveryFailed.Contains(KeyOf(_document));
        _identity.Text = $"{_document.DisplayName} · {_id} · flag 0x{(current?.Flag ?? original?.Flag ?? 0):X2} (preserved)\nUTF-8 · {(_document.Compressed ? "BWT source" : "plain source")}";
        _source.Text = original?.Text ?? "Not present in the source file.";
        _refreshing = false; UpdateState();
    }
    private void UpdateState()
    {
        if (_document == null) return;
        var fields = _raw.Where(field => field.Key.Document == _document).ToArray();
        bool pending = fields.Length > 0;
        var invalid = fields.FirstOrDefault(field => Error(field.Value).Length > 0);
        string error = invalid.Key.Document != null ? $"String {invalid.Key.Id}: {Error(invalid.Value)}" : "";
        _status.Text = error.Length > 0 ? error : _recoveryErrors.GetValueOrDefault(KeyOf(_document), _document.TokenWarning(_id, _text.Text));
        _state.Text = error.Length > 0 ? error : _document.Get(_id) == null ? "Removed from working copy · Undo or Revert to restore."
            : _document.Recovered ? "Recovered unsaved document · Save copy to keep it." : pending || _document.IsDirty ? "Unsaved language document" : "No unsaved changes";
        _identity.TooltipText = $"{Encoding.UTF8.GetByteCount(_text.Text)} / 32767 UTF-8 bytes";
        _save.Disabled = _recoveryFailed.Contains(KeyOf(_document)) || error.Length > 0 || (!pending && !_document.IsDirty);
        _undo.Disabled = !_document.CanUndo && !pending; _redo.Disabled = !_document.CanRedo;
        _delete.Disabled = _document.Get(_id) == null;
    }
    private void Notify()
    {
        Changed?.Invoke();
        Action callback = _changed; ClilocDocument document = _document; int id = _id;
        if (callback != null && IsInsideTree()) Callable.From(() =>
        { if (GodotObject.IsInstanceValid(this) && IsInsideTree() && _document == document && _id == id) callback(); }).CallDeferred();
    }
    private void ConfirmDelete()
    {
        if (_document == null || !CommitPending()) return;
        var dialog = new ConfirmationDialog { Title = "Remove working string?", DialogText = $"Remove {_id} from this {_document.Language} working document? The installed file is unchanged; Undo restores it.", OkButtonText = "Remove from working copy" };
        AddChild(dialog); dialog.Canceled += dialog.QueueFree;
        ClilocDocument document = _document; int id = _id;
        dialog.Confirmed += () => { document.Delete(id); dialog.QueueFree(); Read(); Notify(); }; dialog.PopupCentered();
    }
    private void Shortcut(InputEvent input)
    {
        if (input is InputEventKey key && key.Pressed && !key.Echo && (key.CtrlPressed || key.MetaPressed) && key.Keycode == Key.S)
        { Save(); GetViewport().SetInputAsHandled(); }
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (!IsVisibleInTree() || _document == null || input is not InputEventKey key || !key.Pressed || key.Echo || !(key.CtrlPressed || key.MetaPressed)) return;
        if (key.Keycode == Key.S) { Save(); GetViewport().SetInputAsHandled(); }
        else if (key.Keycode == Key.Z && GetViewport().GuiGetFocusOwner() is not TextEdit)
        { if (key.ShiftPressed) { if (CommitPending()) _document.Redo(); Read(); Notify(); } else Undo(); GetViewport().SetInputAsHandled(); }
    }

    private static string KeyOf(ClilocDocument document) => document.RecoveryIdentity;
    internal void Discard(ClilocDocument document)
    {
        foreach (var key in _raw.Keys.Where(key => key.Document == document).ToArray()) _raw.Remove(key);
        _documents.Remove(document); string identity = KeyOf(document);
        _recoveryFailed.Remove(identity); _recoveryErrors.Remove(identity);
        var rows = new JsonArray();
        foreach (JsonNode row in Drafts()) if ((string)row["key"] != identity) rows.Add(row.DeepClone());
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_cliloc", "drafts", rows.ToJsonString());
        if (_document == document) { _document = null; _changed = null; }
    }
    private static JsonArray Drafts()
    {
        string value = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_cliloc", "drafts", "[]").AsString();
        try { return value.Length <= 16 * 1024 * 1024 ? JsonNode.Parse(value) as JsonArray ?? new() : new(); }
        catch { return new(); }
    }
    private void RestoreDraft(ClilocDocument document)
    {
        string key = KeyOf(document);
        foreach (JsonNode row in Drafts())
            if ((string)row["key"] == key)
            {
                try
                {
                    var pending = new Dictionary<int, string>();
                    foreach (JsonNode raw in row["raw"].AsArray())
                    {
                        if ((int?)raw["id"] is not int id || id < 0 || (string)raw["text"] is not string text || !pending.TryAdd(id, text))
                            throw new System.IO.IOException("Invalid or duplicate recovered text draft.");
                    }
                    document.Restore(row["snapshot"], false);
                    foreach (var raw in pending) _raw[(document, raw.Key)] = raw.Value;
                }
                catch (Exception e) { _recoveryFailed.Add(key); _recoveryErrors[key] = "Draft recovery refused: " + e.Message + " Use Reopen to discard the failed recovery entry."; }
                break;
            }
    }
    public void StashDrafts()
    {
        if (_documents.Count == 0) return;
        var known = _documents.Select(KeyOf).Where(key => !_recoveryFailed.Contains(key)).ToHashSet();
        var rows = new JsonArray(); foreach (JsonNode row in Drafts()) if (!known.Contains((string)row["key"])) rows.Add(row.DeepClone());
        foreach (ClilocDocument document in _documents)
        {
            if (_recoveryFailed.Contains(KeyOf(document))) continue;
            var raw = new JsonArray();
            foreach (var field in _raw.Where(field => field.Key.Document == document)) raw.Add(new JsonObject { ["id"] = field.Key.Id, ["text"] = field.Value });
            if (document.IsDirty || raw.Count > 0) rows.Add(new JsonObject { ["key"] = KeyOf(document), ["snapshot"] = document.Snapshot(), ["raw"] = raw });
        }
        string json = rows.ToJsonString();
        if (json.Length <= 16 * 1024 * 1024) EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_cliloc", "drafts", json);
        else if (_state != null) _state.Text = "Draft recovery exceeds 16 MiB. Save valid language copies before closing.";
    }
    public override void _ExitTree() => StashDrafts();
}
#endif
