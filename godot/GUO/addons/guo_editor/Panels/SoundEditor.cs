#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Inline sound naming with retained invalid drafts and editor-local recovery.</summary>
[Tool]
public partial class SoundEditor : VBoxContainer
{
    private readonly HashSet<SoundDocument> _documents = new();
    private SoundDocument _document;
    private LineEdit _name;
    private Label _source, _status, _state;
    private Button _undo, _redo, _save;
    private Action _changed;
    private bool _reading;
    internal VBoxContainer Footer { get; private set; }
    internal SoundDocument Document => _document;
    internal LineEdit NameInput => _name;
    internal Button SaveButton => _save;
    public event Action Changed;
    public string Summary => _document == null ? "Select a sound effect." : $"Sound 0x{_document.Id:X4} · {(_document.IsDirty || _document.Pending ? "unsaved naming document" : "saved naming document")}";

    public override void _Ready()
    {
        if (_name != null) return;
        AddChild(new Label { Text = "Working name" });
        _name = new LineEdit { MaxLength = 1024, AccessibilityName = "Sound effect working name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_name); _name.TextChanged += value => { if (_reading || _document == null) return; _document.DraftName = value; State(); Changed?.Invoke(); };
        _name.TextSubmitted += _ => CommitPending(); _name.FocusExited += () => CommitPending(); _name.GuiInput += SaveShortcut;
        _source = AssetUi.Label(""); AddChild(_source);
        _status = AssetUi.Label(""); AddChild(_status);
        Footer = new VBoxContainer(); AddChild(Footer); _state = AssetUi.Label(""); Footer.AddChild(_state);
        var bar = new HBoxContainer(); Footer.AddChild(bar);
        _undo = AssetUi.Action("Undo", "Undo sound name edit", Undo); bar.AddChild(_undo);
        _redo = AssetUi.Action("Redo", "Redo sound name edit", () => { _document?.Redo(); Read(); Notify(); }); bar.AddChild(_redo);
        bar.AddChild(AssetUi.Action("Reload", "Revert sound name to source (undoable)", () => { _document?.Revert(); Read(); Notify(); }));
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _save = AssetUi.Action("Save", "Save sound naming document · Ctrl+S", () => Save()); _save.Text = "Save copy"; bar.AddChild(_save);
    }
    internal void Bind(SoundDocument document, Action changed)
    {
        if (_name == null) _Ready(); CommitPending(); _document = document; _changed = changed;
        if (_documents.Add(document))
        {
            JsonNode row = Drafts().FirstOrDefault(row => (string)row?["key"] == document.RecoveryKey);
            if (row != null) try { document.Restore(row); } catch (Exception e)
            { document.RecoveryError = "Draft recovery refused: " + e.Message + " Use Reopen sound to discard this entry."; }
        }
        Read();
    }
    internal bool CommitPending()
    {
        if (_document == null || !_document.Pending) return true;
        bool ok = _document.CommitDraft(); State(); if (ok) Notify(); return ok;
    }
    internal bool Save()
    {
        if (_document == null) return false;
        try { _document.Save(); StashDrafts(); Read(); _state.Text = "Saved project name. Native sound and PCM unchanged."; Notify(); return true; }
        catch (Exception e) { _status.Text = e.Message; return false; }
    }
    internal void Undo() { _document?.Undo(); Read(); Notify(); }
    private void Read()
    {
        if (_document == null || _name == null) return;
        _reading = true; _name.Text = _document.DraftName; _name.Editable = _document.RecoveryError == null;
        _source.Text = "Source name: " + _document.SourceName; _reading = false; State();
    }
    private void State()
    {
        if (_document == null) return;
        _status.Text = _document.RecoveryError ?? _document.Validation;
        _status.Visible = _status.Text.Length > 0;
        _state.Text = _document.IsDirty || _document.Pending ? "Unsaved sound name · PCM unchanged" : "No unsaved changes";
        _name.TooltipText = Encoding.UTF8.GetByteCount(_document.DraftName ?? "") + " / 32 UTF-8 bytes";
        _save.Disabled = _document.RecoveryError != null || _document.Validation.Length > 0 || (!_document.Pending && !_document.IsDirty);
        _undo.Disabled = _document.RecoveryError != null || (!_document.CanUndo && !_document.Pending);
        _redo.Disabled = _document.RecoveryError != null || !_document.CanRedo;
    }
    private void Notify()
    {
        Changed?.Invoke(); Action callback = _changed; var document = _document;
        if (callback != null && IsInsideTree()) Callable.From(() =>
        { if (IsInstanceValid(this) && IsInsideTree() && _document == document) callback(); }).CallDeferred();
    }
    private void SaveShortcut(InputEvent input)
    {
        if (input is InputEventKey key && key.Pressed && !key.Echo && (key.CtrlPressed || key.MetaPressed) && key.Keycode == Key.S)
        { Save(); GetViewport().SetInputAsHandled(); }
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (!IsVisibleInTree() || _document == null || input is not InputEventKey key || !key.Pressed || key.Echo || !(key.CtrlPressed || key.MetaPressed)) return;
        if (key.Keycode == Key.S) { Save(); GetViewport().SetInputAsHandled(); }
        else if (key.Keycode == Key.Z && GetViewport().GuiGetFocusOwner() is not LineEdit)
        { if (key.ShiftPressed) { _document.Redo(); Read(); Notify(); } else Undo(); GetViewport().SetInputAsHandled(); }
    }
    private static JsonArray Drafts()
    {
        string text = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_sound", "drafts", "[]").AsString();
        try { return text.Length <= 4 * 1024 * 1024 ? JsonNode.Parse(text) as JsonArray ?? new() : new(); }
        catch { return new(); }
    }
    internal void Discard(SoundDocument document)
    {
        _documents.Remove(document); var rows = new JsonArray();
        foreach (JsonNode row in Drafts()) if ((string)row?["key"] != document.RecoveryKey) rows.Add(row.DeepClone());
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_sound", "drafts", rows.ToJsonString());
        if (_document == document) { _document = null; _changed = null; }
    }
    public void StashDrafts()
    {
        if (_documents.Count == 0) return;
        var known = _documents.Where(d => d.RecoveryError == null).Select(d => d.RecoveryKey).ToHashSet();
        var rows = new JsonArray(); foreach (JsonNode row in Drafts()) if (!known.Contains((string)row?["key"])) rows.Add(row.DeepClone());
        foreach (var document in _documents)
            if (document.RecoveryError == null && (document.Pending || document.IsDirty)) rows.Add(document.Recovery());
        string json = rows.ToJsonString();
        if (json.Length <= 4 * 1024 * 1024) EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_sound", "drafts", json);
        else if (_state != null) _state.Text = "Sound draft recovery exceeds 4 MiB. Save valid copies before closing.";
    }
    public override void _ExitTree() => StashDrafts();
}
#endif
