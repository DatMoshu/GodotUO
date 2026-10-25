#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Texture document status and a pinned history/save footer.</summary>
[Tool]
public partial class TextureEditor : VBoxContainer
{
    private readonly HashSet<TextureDocument> _documents = new();
    private TextureDocument _document;
    private Label _status, _state;
    private Button _undo, _redo, _save, _revert;
    private Action _changed;
    internal TextureDocument Document => _document;
    internal VBoxContainer Footer { get; private set; }
    internal Button SaveButton => _save;
    public event Action Changed;
    public string Summary => _document == null ? "Select a texture." : $"Texture 0x{_document.Id:X4} · {(_document.IsDirty ? "unsaved document" : "no unsaved changes")} · {_document.Size} × {_document.Size}";
    public override void _Ready()
    {
        if (_status != null) return;
        _status = AssetUi.Label(""); AddChild(_status);
        Footer = new VBoxContainer(); AddChild(Footer); _state = AssetUi.Label(""); Footer.AddChild(_state);
        var row = new HBoxContainer(); Footer.AddChild(row);
        _undo = AssetUi.Action("Undo", "Undo texture replacement", Undo); row.AddChild(_undo);
        _redo = AssetUi.Action("Redo", "Redo texture replacement", () => { _document?.Redo(); Notify(); }); row.AddChild(_redo);
        _revert = AssetUi.Action("Reload", "Revert texture to source (undoable)", () => { _document?.Revert(); Notify(); }); row.AddChild(_revert);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _save = AssetUi.Action("Save", "Save texture working document · Ctrl+S", () => Save()); _save.Text = "Save copy"; row.AddChild(_save);
    }
    internal void Bind(TextureDocument document, Action changed)
    {
        if (_status == null) _Ready(); _document = document; _changed = changed;
        bool restored = false;
        if (_documents.Add(document))
        {
            JsonNode row = Drafts().FirstOrDefault(row => (string)row?["key"] == document.RecoveryKey);
            if (row != null) try { document.Restore(row); restored = true; } catch (Exception e)
            { document.RecoveryError = "Draft recovery refused: " + e.Message + " Use Reopen texture to discard this entry."; }
        }
        State(); if (restored) Notify();
    }
    internal bool Save()
    {
        if (_document == null) return false;
        try { _document.Save(); StashDrafts(); Notify(); return true; }
        catch (Exception e) { _status.Text = e.Message; _status.Visible = true; return false; }
    }
    internal void Undo() { _document?.Undo(); Notify(); }
    private void State()
    {
        if (_document == null) return;
        _status.Text = _document.RecoveryError ?? _document.SourceError; _status.Visible = _status.Text.Length > 0;
        bool blocked = _status.Visible;
        _state.Text = Summary; _save.Disabled = blocked || !_document.IsDirty;
        _undo.Disabled = blocked || !_document.CanUndo; _redo.Disabled = blocked || !_document.CanRedo; _revert.Disabled = blocked;
    }
    private void Notify()
    {
        State(); Changed?.Invoke(); Action callback = _changed; var document = _document;
        if (callback != null && IsInsideTree()) Callable.From(() =>
        { if (IsInstanceValid(this) && IsInsideTree() && _document == document) callback(); }).CallDeferred();
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (!IsVisibleInTree() || _document == null || input is not InputEventKey key || !key.Pressed || key.Echo || !(key.CtrlPressed || key.MetaPressed)) return;
        if (key.Keycode == Key.S) { Save(); GetViewport().SetInputAsHandled(); }
        else if (key.Keycode == Key.Z && GetViewport().GuiGetFocusOwner() is not LineEdit)
        { if (key.ShiftPressed) { _document.Redo(); Notify(); } else Undo(); GetViewport().SetInputAsHandled(); }
    }
    private static JsonArray Drafts()
    {
        string json = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_textures", "drafts", "[]").AsString();
        try { return json.Length <= 4 * 1024 * 1024 ? JsonNode.Parse(json) as JsonArray ?? new() : new(); } catch { return new(); }
    }
    internal void Discard(TextureDocument document)
    {
        _documents.Remove(document); var rows = new JsonArray();
        foreach (JsonNode row in Drafts()) if ((string)row?["key"] != document.RecoveryKey) rows.Add(row.DeepClone());
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_textures", "drafts", rows.ToJsonString());
        if (_document == document) { _document = null; _changed = null; }
    }
    internal void StashDrafts()
    {
        var rows = (JsonArray)Drafts().DeepClone();
        foreach (var document in _documents)
        {
            if (document.RecoveryError != null) continue;
            foreach (JsonNode row in rows.Where(row => (string)row?["key"] == document.RecoveryKey).ToArray()) rows.Remove(row);
            if (document.IsDirty) rows.Add(document.Recovery());
        }
        string json = rows.ToJsonString();
        if (json.Length <= 4 * 1024 * 1024) EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_textures", "drafts", json);
        else if (_status != null) { _status.Text = "Texture recovery exceeds 4 MiB. Save copies before closing."; _status.Visible = true; }
    }
    public override void _ExitTree() => StashDrafts();
}
#endif
