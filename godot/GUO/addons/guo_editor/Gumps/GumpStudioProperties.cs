#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

public partial class GumpStudio
{
    private bool _rebuildingProperties;
    private void RefreshProperties()
    {
        _rebuildingProperties = true;
        foreach (Node child in _properties.GetChildren()) { _properties.RemoveChild(child); child.QueueFree(); }
        _rebuildingProperties = false;
        var selected = Selected().ToArray();
        _properties.AddChild(new Label { Text = selected.Length == 0 ? "DOCUMENT" : $"PROPERTIES · {selected.Length} selected" });
        if (selected.Length == 0)
        {
            StringField("Name", _document.Name, s => _document.Name = s);
            NumberField("Width", _document.Width, 16, 8192, v => _document.Width = v);
            NumberField("Height", _document.Height, 16, 8192, v => _document.Height = v);
            BoolField("Modern Godot controls", _document.Modern, v => _document.Modern = v);
            _properties.AddChild(new Label { Text = "Drag to move • bottom-right handle to resize\nShift-click for multiple selection\nPage 0 is visible on all pages\nInteract tests replies without sending packets", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            if (_document.Notes.Count > 0)
                _properties.AddChild(new Label { Text = string.Join("\n", _document.Notes.Take(15)), AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }
        if (selected.Length > 1)
        {
            _properties.AddChild(new Label { Text = "Move together on the canvas.\nUse Align left / Align top." }); return;
        }
        var e = selected[0];
        StringField("Name", e.Name, s => e.Name = s);
        StringField("Display binding (optional)", e.Binding, s => e.Binding = s);
        _properties.AddChild(new Label { Text = e.Kind.ToString() });
        NumberField("X", e.X, -32767, 32767, v => e.X = v);
        NumberField("Y", e.Y, -32767, 32767, v => e.Y = v);
        NumberField("Width", e.Width, 1, 8192, v => e.Width = v);
        NumberField("Height", e.Height, 1, 8192, v => e.Height = v);
        NumberField("Page", e.Page, 0, 65535, v => e.Page = v);
        BoolField("Visible", e.Visible, v => e.Visible = v);
        BoolField("Locked on canvas", e.Locked, v => e.Locked = v);
        ChoiceField("Anchor (modern)", e.Anchor, new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Stretch" }, s => e.Anchor = s);
        if (e.Kind is GumpElementKind.Image or GumpElementKind.TiledImage or GumpElementKind.Panel or GumpElementKind.Button or GumpElementKind.CheckBox or GumpElementKind.Radio)
        {
            NumberField("Gump art ID", e.Graphic, 0, 65535, v => e.Graphic = v);
            AddButton(_properties, "Choose gump art…", () => PickGumpArt(id => ChangeProperty(() => e.Graphic = id)));
        }
        if (e.Kind is GumpElementKind.Button or GumpElementKind.CheckBox or GumpElementKind.Radio)
        {
            NumberField("Pressed / checked art", e.GraphicDown, 0, 65535, v => e.GraphicDown = v);
            AddButton(_properties, "Choose pressed art…", () => PickGumpArt(id => ChangeProperty(() => e.GraphicDown = id)));
        }
        if (e.Kind is GumpElementKind.Label or GumpElementKind.Html or GumpElementKind.Button or GumpElementKind.TextEntry or GumpElementKind.CheckBox or GumpElementKind.Radio)
        {
            TextField("Text", e.Text, s => e.Text = s);
            NumberField("Font size (modern)", e.FontSize, 8, 128, v => e.FontSize = v);
            StringField("Text color (hex)", e.Color, s => e.Color = s);
            NumberField("Classic hue", e.Hue, 0, 65535, v => e.Hue = v);
        }
        if (e.Kind == GumpElementKind.Panel) StringField("Background (hex)", e.Background, s => e.Background = s);
        if (e.Kind is GumpElementKind.Button or GumpElementKind.CheckBox or GumpElementKind.Radio)
            NumberField("Reply / switch ID", e.ReplyId, 0, int.MaxValue, v => e.ReplyId = v);
        if (e.Kind == GumpElementKind.Button)
        {
            BoolField("Switch page (no reply)", e.PageButton, v => e.PageButton = v);
            NumberField("Target page", e.TargetPage, 0, 65535, v => e.TargetPage = v);
        }
        if (e.Kind is GumpElementKind.CheckBox or GumpElementKind.Radio)
        {
            BoolField("Initially checked", e.Checked, v => e.Checked = v);
            NumberField("Radio group", e.Group, 0, 65535, v => e.Group = v);
        }
        if (e.Kind == GumpElementKind.TextEntry)
        {
            NumberField("Entry ID", e.EntryId, 0, 65535, v => e.EntryId = v);
            NumberField("Max length", e.MaxLength, 0, 65535, v => e.MaxLength = v);
        }
        if (e.Kind == GumpElementKind.Raw)
        {
            TextField("Preserved classic command", e.Raw, s => e.Raw = s);
            _properties.AddChild(new Label { Text = "Preserved on classic export; not rendered.\nPosition fields do not rewrite raw commands.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }
    }
    private void ChangeProperty(Action action)
    {
        _history.Push(_document); action();
        // Leave the inspector alive while typing; replacing a focused native control loses keystrokes.
        RefreshCanvas(); RefreshLayers(); _title.Text = _document.Name + (Dirty ? " *" : "");
    }
    private void StringField(string label, string value, Action<string> set)
    {
        _properties.AddChild(new Label { Text = label });
        var edit = new LineEdit { Text = value }; _properties.AddChild(edit);
        edit.TextSubmitted += s => { if (s != value) { ChangeProperty(() => set(s)); value = s; } };
        edit.FocusExited += () => { if (!_rebuildingProperties && GodotObject.IsInstanceValid(edit) && edit.IsInsideTree() && edit.Text != value) { string s = edit.Text; ChangeProperty(() => set(s)); value = s; } };
    }
    private void NumberField(string label, int value, double min, double max, Action<int> set)
    {
        var row = new HBoxContainer(); _properties.AddChild(row);
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, TooltipText = label });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, CustomMinimumSize = new Vector2(112, 0) }; row.AddChild(spin);
        spin.ValueChanged += v => ChangeProperty(() => set((int)v));
    }
    private void BoolField(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Text = label, ButtonPressed = value }; _properties.AddChild(check);
        check.Toggled += v => ChangeProperty(() => set(v));
    }
    private void ChoiceField(string label, string value, string[] choices, Action<string> set)
    {
        _properties.AddChild(new Label { Text = label });
        var choice = new OptionButton(); foreach (string option in choices) choice.AddItem(option);
        choice.Selected = Math.Max(0, Array.IndexOf(choices, value)); _properties.AddChild(choice);
        choice.ItemSelected += i => ChangeProperty(() => set(choices[i]));
    }
    private void TextField(string label, string value, Action<string> set)
    {
        _properties.AddChild(new Label { Text = label });
        var edit = new TextEdit { Text = value, CustomMinimumSize = new Vector2(240, 90), WrapMode = TextEdit.LineWrappingMode.Boundary }; _properties.AddChild(edit);
        AddButton(_properties, "Apply " + label.ToLowerInvariant(), () => ChangeProperty(() => set(edit.Text)));
    }
}
#endif
