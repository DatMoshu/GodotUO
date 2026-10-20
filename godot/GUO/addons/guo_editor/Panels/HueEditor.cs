#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Inline palette authoring, with document-owned raw drafts and a pinned save footer.</summary>
[Tool]
public partial class HueEditor : VBoxContainer
{
    private readonly HashSet<HueDocument> _documents = new();
    private HueDocument _document;
    private LineEdit _name, _start, _end, _hex;
    private Label _index, _status, _state;
    private GridContainer _palette;
    private ColorPickerButton _picker;
    private Button _undo, _redo, _save, _apply;
    private bool _reading;
    private Action _changed;
    private SpinBox _rangeStart, _rangeEnd, _red, _green, _blue;
    private OptionButton _operation;
    private HBoxContainer _shifts;
    internal VBoxContainer Operations { get; private set; }
    internal CheckButton OperationsToggle { get; private set; }
    internal Button RangeApply { get; private set; }
    internal HueDocument Document => _document;
    internal LineEdit NameInput => _name;
    internal LineEdit ColorInput => _hex;
    internal ColorPickerButton Picker => _picker;
    internal Button SaveButton => _save;
    internal GridContainer Palette => _palette;
    internal VBoxContainer Footer { get; private set; }
    public event Action Changed;
    public string Summary => _document == null ? "Select a hue." : $"Hue 0x{_document.Id:X4} · {(_document.IsDirty || _document.Pending ? "unsaved palette" : "saved palette")} · selected color {_document.Index}";

    public override void _Ready()
    {
        if (_name != null) return;
        AddChild(new Label { Text = "Working hue name" });
        _name = Field("Hue working name", value => _document.DraftName = value);
        _palette = new GridContainer { Columns = 8, TextureFilter = TextureFilterEnum.Nearest }; AddChild(_palette);
        for (int i = 0; i < 32; i++)
        {
            int index = i;
            var button = new Button { ToggleMode = true, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest,
                CustomMinimumSize = new Vector2(26, 26) * EditorInterface.Singleton.GetEditorScale() };
            button.Pressed += () => { if (_document != null) { _document.SelectColor(index); Read(); Notify(); } };
            _palette.AddChild(button);
        }
        var selected = new HBoxContainer(); AddChild(selected);
        _index = new Label(); selected.AddChild(_index);
        _hex = Field("Selected raw 16-bit hue color", value => _document.DraftColor = value, selected);
        _apply = AssetUi.Action("GuiChecked", "Apply typed color and hue fields as one undo step", () => CommitPending()); selected.AddChild(_apply);
        _picker = new ColorPickerButton { EditAlpha = false, AccessibilityName = "Pick RGB for the selected hue color", TooltipText = "Pick RGB; commit once when the picker closes. Untouched bit 15 is preserved.",
            CustomMinimumSize = new Vector2(0, 30) * EditorInterface.Singleton.GetEditorScale(), SizeFlagsHorizontal = SizeFlags.ExpandFill }; AddChild(_picker);
        _picker.PopupClosed += ApplyPickedColor;
        OperationsToggle = new CheckButton { Text = "Palette operations", TooltipText = "Gradients, reverse and RGB shift on an inclusive palette range" }; AddChild(OperationsToggle);
        Operations = new VBoxContainer { Visible = false }; AddChild(Operations);
        OperationsToggle.Toggled += value => Operations.Visible = value;
        var range = new HBoxContainer(); Operations.AddChild(range);
        range.AddChild(new Label { Text = "From" }); _rangeStart = RangeNumber("First palette index", 0, 31, 0); range.AddChild(_rangeStart);
        range.AddChild(new Label { Text = "To" }); _rangeEnd = RangeNumber("Last palette index", 0, 31, 31); range.AddChild(_rangeEnd);
        var command = new HBoxContainer(); Operations.AddChild(command);
        _operation = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = "Palette range operation" };
        foreach (string text in new[] { "Spread gradient", "Exponential gradient", "Reverse range", "Shift RGB" }) _operation.AddItem(text);
        command.AddChild(_operation); RangeApply = AssetUi.Action("GuiChecked", "Apply selected operation to the inclusive palette range as one undo step", () => ApplyRange()); command.AddChild(RangeApply);
        _shifts = new HBoxContainer { Visible = false }; Operations.AddChild(_shifts);
        SpinBox Shift(string label) { var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _shifts.AddChild(column); column.AddChild(new Label { Text = label }); var input = RangeNumber(label + " channel shift in 8-bit units", -255, 255, 0); column.AddChild(input); return input; }
        _red = Shift("R"); _green = Shift("G"); _blue = Shift("B");
        _operation.ItemSelected += value => _shifts.Visible = value == (int)HueRangeOperation.ShiftRgb;
        Operations.AddChild(AssetUi.Label("RGB operations write 15-bit colors. Reverse preserves all 16 bits. Gradients need two indices with at least one color between them."));
        var advanced = new VBoxContainer { Visible = false };
        var show = new CheckButton { Text = "Advanced · raw table bounds" }; AddChild(show); AddChild(advanced); show.Toggled += value => advanced.Visible = value;
        advanced.AddChild(new Label { Text = "Table start (hex)" }); _start = Field("Hue table start", value => _document.DraftStart = value, advanced);
        advanced.AddChild(new Label { Text = "Table end (hex)" }); _end = Field("Hue table end", value => _document.DraftEnd = value, advanced);
        _status = AssetUi.Label(""); AddChild(_status);
        Footer = new VBoxContainer(); AddChild(Footer); _state = AssetUi.Label(""); Footer.AddChild(_state);
        var bar = new HBoxContainer(); Footer.AddChild(bar);
        _undo = AssetUi.Action("Undo", "Undo hue edit", Undo); bar.AddChild(_undo);
        _redo = AssetUi.Action("Redo", "Redo hue edit", () => { _document?.Redo(); Read(); Notify(); }); bar.AddChild(_redo);
        bar.AddChild(AssetUi.Action("Reload", "Revert palette and name to loaded base hue (undoable)", () => { try { _document?.Revert(); Read(); Notify(); } catch (Exception e) { Error(e.Message); } }));
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _save = AssetUi.Action("Save", "Save hue project copy · Ctrl+S while editing fields", () => Save()); _save.Text = "Save copy"; bar.AddChild(_save);
    }
    private LineEdit Field(string label, Action<string> change, Node parent = null)
    {
        var input = new LineEdit { AccessibilityName = label, MaxLength = 8192, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        (parent ?? this).AddChild(input);
        input.TextChanged += value => { if (_reading || _document == null) return; change(value); State(); Changed?.Invoke(); };
        input.TextSubmitted += _ => CommitPending(); input.FocusExited += () => CommitPending();
        input.GuiInput += e => { if (e is InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, Keycode: Key.S }) { Save(); GetViewport().SetInputAsHandled(); } };
        return input;
    }
    private static SpinBox RangeNumber(string label, int min, int max, int value) => new()
    { MinValue = min, MaxValue = max, Step = 1, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = label, TooltipText = label };
    internal void ChooseRange(int first, int last, HueRangeOperation operation, int red = 0, int green = 0, int blue = 0)
    {
        OperationsToggle.SetPressedNoSignal(true); Operations.Visible = true;
        _rangeStart.Value = first; _rangeEnd.Value = last; _operation.Selected = (int)operation;
        _shifts.Visible = operation == HueRangeOperation.ShiftRgb; _red.Value = red; _green.Value = green; _blue.Value = blue;
    }
    internal bool ApplyRange()
    {
        if (_document == null) return false;
        try { HueRange.Apply(_document, (int)_rangeStart.Value, (int)_rangeEnd.Value, (HueRangeOperation)_operation.Selected, (int)_red.Value, (int)_green.Value, (int)_blue.Value); Read(); Notify(); return true; }
        catch (Exception e) { Error(e.Message); return false; }
    }
    internal void Bind(HueDocument document, Action changed)
    {
        if (_name == null) _Ready(); CommitPending(); _document = document; _changed = changed;
        if (_documents.Add(document))
        {
            JsonNode row = Drafts().FirstOrDefault(row => (string)row?["key"] == document.RecoveryKey);
            if (row != null) try { document.Restore(row); } catch (Exception e) { document.RecoveryError = "Hue recovery refused: " + e.Message + " Use Reopen hue to discard this draft."; }
        }
        Read();
    }
    internal bool CommitPending()
    {
        if (_document == null || !_document.Pending) return true;
        bool ok = _document.CommitDraft(); if (ok) { Read(); Notify(); } else State(); return ok;
    }
    internal bool Save()
    {
        if (_document == null) return false;
        try { _document.Save(); StashDrafts(); Read(); _state.Text = "Saved project hue copy. Source files unchanged."; Notify(); return true; }
        catch (Exception e) { Error(e.Message); return false; }
    }
    internal void Undo() { _document?.Undo(); Read(); Notify(); }
    internal void ApplyPickedColor()
    {
        if (_reading || _document == null) return;
        Color color = _picker.Color;
        if (!_document.CommitDraft()) { State(); return; }
        ushort value = (ushort)((_document.ColorAt(_document.Index) & 0x8000) | ((color.R8 >> 3) << 10) | ((color.G8 >> 3) << 5) | (color.B8 >> 3));
        _document.DraftColor = $"0x{value:X4}"; _document.CommitDraft(); Read(); Notify();
    }
    private static Color DisplayColor(ushort value)
    {
        uint rgb = GUO.Utility.HuesHelper.Color16To32(value);
        return Color.Color8((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
    }
    private void Read()
    {
        if (_document == null || _name == null) return;
        _reading = true; _name.Text = _document.DraftName; _start.Text = _document.DraftStart; _end.Text = _document.DraftEnd; _hex.Text = _document.DraftColor;
        _index.Text = $"Color {_document.Index}"; _picker.Color = DisplayColor(_document.ColorAt(_document.Index));
        for (int i = 0; i < 32; i++)
        {
            var button = _palette.GetChild<Button>(i); ushort value = _document.ColorAt(i);
            using Image image = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8); image.Fill(DisplayColor(value));
            var old = button.Icon; button.Icon = ImageTexture.CreateFromImage(image); old?.Dispose();
            button.SetPressedNoSignal(i == _document.Index); button.TooltipText = button.AccessibilityName = $"Palette index {i} · 0x{value:X4}";
        }
        _reading = false; State();
    }
    private void State()
    {
        if (_document == null) return;
        string source = _document.SourceError;
        string error = _document.RecoveryError ?? (source.Length > 0 ? source : _document.Validation);
        _status.Text = error.Length > 0 ? error : _document.NameWarning; _status.Visible = _status.Text.Length > 0;
        _state.Text = _document.IsDirty || _document.Pending ? "Unsaved hue palette" : "No unsaved hue changes";
        _name.TooltipText = $"{Encoding.UTF8.GetByteCount(_document.DraftName ?? ""):N0} / 4,096 UTF-8 project bytes · native names use 20 ASCII bytes";
        _save.Disabled = error.Length > 0 || (!_document.Pending && !_document.IsDirty);
        _undo.Disabled = _document.RecoveryError != null || (!_document.CanUndo && !_document.Pending);
        _redo.Disabled = _document.RecoveryError != null || !_document.CanRedo || _document.Pending;
        _apply.Disabled = _picker.Disabled = error.Length > 0;
        RangeApply.Disabled = error.Length > 0;
        _name.Editable = _start.Editable = _end.Editable = _hex.Editable = _document.RecoveryError == null;
    }
    private void Error(string message) { _status.Text = message; _status.Visible = true; }
    private void Notify()
    {
        Changed?.Invoke(); Action callback = _changed; var document = _document;
        if (callback != null && IsInsideTree()) Callable.From(() => { if (IsInstanceValid(this) && IsInsideTree() && _document == document) callback(); }).CallDeferred();
    }
    private static JsonArray Drafts()
    {
        string text = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_hues", "drafts", "[]").AsString();
        try { return text.Length <= 4 * 1024 * 1024 ? JsonNode.Parse(text) as JsonArray ?? new() : new(); } catch { return new(); }
    }
    internal void Discard(HueDocument document)
    {
        _documents.Remove(document); var rows = new JsonArray();
        foreach (JsonNode row in Drafts()) if ((string)row?["key"] != document.RecoveryKey) rows.Add(row.DeepClone());
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_hues", "drafts", rows.ToJsonString());
        if (_document == document) { _document = null; _changed = null; }
    }
    public void StashDrafts()
    {
        if (_documents.Count == 0) return; var rows = (JsonArray)Drafts().DeepClone();
        foreach (var document in _documents)
        {
            if (document.RecoveryError != null) continue;
            JsonObject next = document.Pending || document.IsDirty ? document.Recovery() : null;
            foreach (JsonNode row in rows.Where(row => (string)row?["key"] == document.RecoveryKey).ToArray()) rows.Remove(row);
            if (next != null) rows.Add(next);
        }
        string json = rows.ToJsonString();
        if (json.Length <= 4 * 1024 * 1024) EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_hues", "drafts", json);
        else if (_state != null) _state.Text = "Hue recovery exceeds 4 MiB. Save valid copies before closing.";
    }
    public override void _ExitTree() => StashDrafts();
}
#endif
