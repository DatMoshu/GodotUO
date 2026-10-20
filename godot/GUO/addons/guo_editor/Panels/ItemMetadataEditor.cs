#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using GUO.Assets;
using GUO.Utility;

/// <summary>Docked document editing. Each selected item keeps its own draft and history.</summary>
[Tool]
public partial class ItemMetadataEditor : VBoxContainer
{
    private sealed class Draft
    {
        public ItemMetadataDocument Document;
        public string Source, Root, Install, Name, Flags, Height;
        public bool Pending => Name != Document.Current.Name || Flags != $"0x{Document.Current.Flags:X16}"
            || Height != Document.Current.Height.ToString();
        public bool Dirty => Pending || Document.IsDirty;
        public void Read() { Name = Document.Current.Name; Flags = $"0x{Document.Current.Flags:X16}"; Height = Document.Current.Height.ToString(); }
    }

    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(ulong Mask, CheckBox Box)> _flags = new();
    private Draft _draft;
    private LineEdit _name, _raw, _height;
    private Label _bytes, _status, _state;
    private Button _undo, _redo, _save;
    private bool _refreshing;
    public event Action Changed;
    public int DirtyCount => _drafts.Values.Count(d => d.Dirty);
    public string Summary => DirtyCount > 0 ? $"{DirtyCount} unsaved metadata document(s)" : "Metadata documents saved";
    internal ItemMetadataDocument CurrentDocument => _draft?.Document;
    internal LineEdit NameInput => _name;
    internal LineEdit FlagsInput => _raw;
    internal Button SaveButton => _save;
    internal int DraftCount => _drafts.Count;
    internal VBoxContainer Footer { get; private set; }

    public ItemMetadataEditor()
    {
        Name = "ItemMetadata";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);
    }

    public override void _Ready()
    {
        if (_name != null) return;
        var heading = new HBoxContainer();
        heading.AddChild(new Label { Text = "Name", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _bytes = new Label(); heading.AddChild(_bytes); AddChild(heading);
        _name = new LineEdit { MaxLength = 256, AccessibilityName = "Item name", ClearButtonEnabled = true };
        AddChild(_name);
        var height = new HBoxContainer();
        height.AddChild(new Label { Text = "Height", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _height = new LineEdit { CustomMinimumSize = new Vector2(64, 0) * EditorInterface.Singleton.GetEditorScale(), MaxLength = 4, AccessibilityName = "Item height, 0 to 255" };
        height.AddChild(_height); AddChild(height);
        AddChild(new Label { Text = "Flags" });
        var flags = new GridContainer { Columns = 2 };
        foreach (TileFlag flag in new[] { TileFlag.Wall, TileFlag.Impassable, TileFlag.Surface, TileFlag.Bridge, TileFlag.NoShoot, TileFlag.ArticleA })
        {
            ulong mask = (ulong)flag;
            var check = new CheckBox { Text = flag.ToString(), TooltipText = $"{flag} · 0x{mask:X16}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            check.Toggled += value =>
            {
                if (_refreshing || _draft == null) return;
                if (!CommitPending()) { Refresh(); return; }
                ulong next = value ? _draft.Document.Current.Flags | mask : _draft.Document.Current.Flags & ~mask;
                _draft.Document.Edit(_draft.Document.Current.Name, next, _draft.Document.Current.Height);
                _draft.Read(); Refresh(); Changed?.Invoke();
            };
            flags.AddChild(check); _flags.Add((mask, check));
        }
        AddChild(flags);
        var advanced = new VBoxContainer();
        var toggle = new Button { Text = "Advanced flags", ToggleMode = true, Alignment = HorizontalAlignment.Left, Flat = true, Icon = AssetUi.Icon("GuiTreeArrowRight") };
        toggle.Toggled += open => { advanced.Visible = open; toggle.Icon = AssetUi.Icon(open ? "GuiTreeArrowDown" : "GuiTreeArrowRight"); };
        AddChild(toggle); AddChild(advanced);
        advanced.AddChild(AssetUi.Label("64-bit hexadecimal · unexposed bits are preserved"));
        _raw = new LineEdit { MaxLength = 32, AccessibilityName = "Raw 64-bit item flags" }; advanced.AddChild(_raw);
        advanced.Visible = false;
        foreach (LineEdit input in new[] { _name, _height, _raw })
        {
            input.TextChanged += _ => CaptureInputs();
            input.TextSubmitted += _ => CommitPending();
            input.FocusExited += () => CommitPending();
            input.GuiInput += HandleShortcut;
        }
        _status = AssetUi.Label(""); AddChild(_status);
        Footer = new VBoxContainer(); AddChild(Footer);
        _state = AssetUi.Label(""); Footer.AddChild(_state);
        var bar = new HBoxContainer(); Footer.AddChild(bar);
        _undo = AssetUi.Action("Undo", "Undo metadata edit · Ctrl+Z", Undo);
        _redo = AssetUi.Action("Redo", "Redo metadata edit · Ctrl+Shift+Z", Redo);
        bar.AddChild(_undo); bar.AddChild(_redo);
        bar.AddChild(AssetUi.Action("Reload", "Revert metadata draft to source values (undoable)", () =>
        { if (_draft == null) return; _draft.Document.Revert(); _draft.Read(); Refresh(); Changed?.Invoke(); }));
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _save = AssetUi.Action("Save", "Save project metadata copy · Ctrl+S", () => Save());
        _save.Text = "Save copy"; bar.AddChild(_save);
    }

    public void Bind(EditorData data, int id)
    {
        if (_name == null) _Ready();
        CommitPending();
        _draft = null;
        try
        {
            if (data.Files.Version < ClientVersion.CV_7090) throw new IOException("Modern tiledata (7.0.9.0 or later) is required for metadata editing.");
            string key = Path.Combine(data.Assets.Root, $"metadata-{id:X4}");
            if (!_drafts.TryGetValue(key, out _draft))
            {
                string source = data.Files.GetUOFilePath("tiledata.mul");
                var document = ItemMetadataDocument.Open(data.Assets.Root, data.ClientData, id, ItemMetadataDocument.ReadRecord(source, id));
                _draft = new Draft { Document = document, Source = source, Root = data.Assets.Root, Install = data.ClientData };
                _draft.Read(); _drafts.Add(key, _draft);
                RestoreDraft(_draft);
            }
            Refresh();
        }
        catch (Exception e) { _draft = null; SetEnabled(false); _status.Text = e.Message; }
        Changed?.Invoke();
    }

    private void CaptureInputs()
    {
        if (_refreshing || _draft == null) return;
        _draft.Name = _name.Text; _draft.Height = _height.Text; _draft.Flags = _raw.Text;
        _status.Text = Validate(); UpdateState(); Changed?.Invoke();
    }

    private string Validate()
    {
        if (_draft == null) return "";
        if (_draft.Name.Contains('\0') || (_draft.Name != _draft.Document.Original.Name && Encoding.UTF8.GetByteCount(_draft.Name) > 20)) return "Name must fit 20 UTF-8 bytes and contain no NUL.";
        if (!int.TryParse(_draft.Height, out int height) || height < 0 || height > 255) return "Height must be a whole number from 0 to 255.";
        try { ItemMetadataDocument.ParseFlags(_draft.Flags); }
        catch (ArgumentException e) { return e.Message; }
        return "";
    }

    internal bool CommitPending()
    {
        if (_draft == null || !_draft.Pending) return true;
        string error = Validate();
        if (error.Length > 0) { _status.Text = error; UpdateState(); return false; }
        _draft.Document.Edit(_draft.Name, ItemMetadataDocument.ParseFlags(_draft.Flags), int.Parse(_draft.Height));
        _draft.Read(); Refresh(); Changed?.Invoke(); return true;
    }

    public bool Save()
    {
        if (_draft == null || !CommitPending()) return false;
        try
        {
            _draft.Document.Save(_draft.Root, _draft.Install, _draft.Source);
            Refresh(); _status.Text = "Saved project document. Native tiledata is unchanged.";
            StashDrafts(); Changed?.Invoke(); return true;
        }
        catch (Exception e) { _status.Text = _state.Text = e.Message; return false; }
    }

    internal void Undo()
    {
        if (_draft == null) return;
        if (!CommitPending()) { _draft.Read(); Refresh(); Changed?.Invoke(); return; }
        _draft.Document.Undo(); _draft.Read(); Refresh(); Changed?.Invoke();
    }

    internal void Redo()
    { if (_draft == null || !CommitPending()) return; _draft.Document.Redo(); _draft.Read(); Refresh(); Changed?.Invoke(); }

    private void SetEnabled(bool enabled)
    {
        foreach (LineEdit field in new[] { _name, _height, _raw }) field.Editable = enabled;
        foreach (var (_, box) in _flags) box.Disabled = !enabled;
        _save.Disabled = !enabled; _undo.Disabled = !enabled; _redo.Disabled = !enabled;
    }

    private void Refresh()
    {
        if (_draft == null) return;
        _refreshing = true; SetEnabled(true);
        _name.Text = _draft.Name; _height.Text = _draft.Height; _raw.Text = _draft.Flags;
        ulong flags = _draft.Document.Current.Flags;
        foreach (var (mask, check) in _flags) check.SetPressedNoSignal((flags & mask) != 0);
        _refreshing = false; _status.Text = Validate(); UpdateState();
    }

    private void UpdateState()
    {
        if (_draft == null) return;
        _bytes.Text = $"{Encoding.UTF8.GetByteCount(_draft.Name)} / 20 bytes";
        string error = Validate();
        _state.Text = error.Length > 0 ? error : _draft.Dirty ? "Unsaved metadata draft" : "Metadata · no unsaved changes";
        _undo.Disabled = !_draft.Document.CanUndo && !_draft.Pending;
        _redo.Disabled = !_draft.Document.CanRedo;
        _save.Disabled = !_draft.Dirty || Validate().Length > 0;
    }

    public IReadOnlyList<(int Id, string State)> Changes(string projectRoot) => _drafts.Values
        .Where(d => string.Equals(d.Root, projectRoot, StringComparison.OrdinalIgnoreCase))
        .Where(d => d.Dirty || d.Document.Current != d.Document.Original)
        .Select(d => (d.Document.Id, d.Dirty ? "Unsaved metadata" : "Saved metadata document")).ToArray();

    // Capture raw fields without committing them. Sync validates every captured draft
    // against its exact source record; unvisited recovered drafts must not disappear.
    internal IReadOnlyList<ItemNameDraft> NameDrafts(string projectRoot)
    {
        var rows = new Dictionary<int, ItemNameDraft>();
        Variant value = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array());
        if (value.VariantType == Variant.Type.Array)
            foreach (Variant entry in value.AsGodotArray())
            {
                if (entry.VariantType != Variant.Type.Dictionary) continue;
                var row = entry.AsGodotDictionary();
                if (!row.TryGetValue("root", out Variant root) || !string.Equals(root.AsString(), projectRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!row.TryGetValue("id", out Variant id) || !row.TryGetValue("hash", out Variant hash)
                    || !row.TryGetValue("name", out Variant name) || !row.TryGetValue("flags", out Variant flags) || !row.TryGetValue("height", out Variant height))
                    throw new IOException("A recovered item metadata draft is incomplete. Open and repair its metadata before synchronizing.");
                int number = id.AsInt32();
                if (!rows.TryAdd(number, new(number, hash.AsString(), name.AsString(), flags.AsString(), height.AsString())))
                    throw new IOException("Duplicate recovered item metadata drafts. Repair recovery before synchronizing.");
            }
        foreach (Draft draft in _drafts.Values.Where(d => string.Equals(d.Root, projectRoot, StringComparison.OrdinalIgnoreCase)))
            if (draft.Dirty) rows[draft.Document.Id] = new(draft.Document.Id, draft.Document.SourceHash, draft.Name, draft.Flags, draft.Height);
            else rows.Remove(draft.Document.Id);
        return rows.Values.OrderBy(row => row.Id).ToArray();
    }

    // Recovery drafts are editor-local, not saved project documents. They survive assembly reload
    // and editor restart without silently accepting or exporting the user's edits.
    public void StashDrafts()
    {
        if (_name == null || _drafts.Count == 0) return;
        var entries = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        Variant previous = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array());
        if (previous.VariantType == Variant.Type.Array)
            foreach (Variant item in previous.AsGodotArray())
            {
                if (item.VariantType != Variant.Type.Dictionary) continue;
                var row = item.AsGodotDictionary();
                if (row.TryGetValue("root", out Variant root) && row.TryGetValue("id", out Variant id)
                    && !_drafts.Values.Any(d => d.Root == root.AsString() && d.Document.Id == id.AsInt32())) entries.Add(row);
            }
        foreach (Draft draft in _drafts.Values.Where(d => d.Dirty))
            entries.Add(new() { ["root"] = draft.Root, ["id"] = draft.Document.Id, ["hash"] = draft.Document.SourceHash,
                ["name"] = draft.Name, ["flags"] = draft.Flags, ["height"] = draft.Height });
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_assets", "metadata_drafts", entries);
    }

    private static void RestoreDraft(Draft draft)
    {
        Variant value = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array());
        if (value.VariantType != Variant.Type.Array) return;
        foreach (Variant entry in value.AsGodotArray())
        {
            if (entry.VariantType != Variant.Type.Dictionary) continue;
            var data = entry.AsGodotDictionary();
            if (!data.TryGetValue("root", out Variant root) || root.AsString() != draft.Root
                || !data.TryGetValue("id", out Variant id) || id.AsInt32() != draft.Document.Id
                || !data.TryGetValue("hash", out Variant hash) || hash.AsString() != draft.Document.SourceHash) continue;
            if (data.TryGetValue("name", out Variant name) && data.TryGetValue("flags", out Variant flags) && data.TryGetValue("height", out Variant height))
            { draft.Name = name.AsString(); draft.Flags = flags.AsString(); draft.Height = height.AsString(); }
        }
    }

    public override void _GuiInput(InputEvent @event)
        => HandleShortcut(@event);

    private void HandleShortcut(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || !(key.CtrlPressed || key.MetaPressed)) return;
        if (key.Keycode == Key.S) { Save(); GetViewport().SetInputAsHandled(); }
        // LineEdit owns its own text undo. Document shortcuts apply outside text fields.
        if (key.Keycode == Key.Z && GetViewport().GuiGetFocusOwner() is not LineEdit)
        { if (key.ShiftPressed) Redo(); else Undo(); GetViewport().SetInputAsHandled(); }
    }

    public override void _ExitTree() => StashDrafts();
}
#endif
