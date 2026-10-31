#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;
using GUO.Utility;

/// <summary>F06a: name/flags/height working copy, never applied to runtime loaders.</summary>
[Tool]
public partial class ItemMetadataDialog : AcceptDialog
{
    private EditorData _data;
    private ItemMetadataDocument _document;
    private string _source;
    private LineEdit _name;
    private LineEdit _flags;
    private SpinBox _height;
    private Label _status;
    private Button _undo;
    private Button _redo;
    private bool _closeQueued;

    public static void Open(EditorData data, int id)
    {
        try
        {
            if (data.Files.Version < ClientVersion.CV_7090) throw new IOException("Modern tiledata (7.0.9.0 or later) is required.");
            string source = data.Files.GetUOFilePath("tiledata.mul");
            var model = ItemMetadataDocument.Open(data.Assets.Root, data.ClientData, id, ItemMetadataDocument.ReadRecord(source, id));
            var dialog = new ItemMetadataDialog();
            dialog.Build(data, model, source);
            GuoEditorPlugin.AssetsMain.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(600, 420));
        }
        catch (Exception e) { AssetActions.ReportEdit(e.Message); }
    }

    public void Build(EditorData data, ItemMetadataDocument document, string source)
    {
        _data = data;
        _document = document;
        _source = source;
        Title = $"Item metadata 0x{document.Id:X4}";
        OkButtonText = "Close";
        DialogHideOnOk = false;
        Confirmed += TryClose;
        Canceled += QueueClose;
        CloseRequested += QueueClose;
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        AddChild(layout);
        layout.AddChild(new Label { Text = "Working document · name, flags and height", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        layout.AddChild(new Label { Text = "Name · at most 20 UTF-8 bytes" });
        _name = new LineEdit();
        layout.AddChild(_name);
        layout.AddChild(new Label { Text = "Flags · hexadecimal, all 64 bits preserved" });
        _flags = new LineEdit { PlaceholderText = "0x0000000000000000" };
        layout.AddChild(_flags);
        layout.AddChild(new Label { Text = "Height" });
        _height = new SpinBox { MinValue = 0, MaxValue = 255, Step = 1 };
        layout.AddChild(_height);
        var history = new HBoxContainer();
        layout.AddChild(history);
        AddButton(history, "Apply to document", () => Capture());
        _undo = AddButton(history, "Undo", () => { _document.Undo(); Refresh(); });
        _redo = AddButton(history, "Redo", () => { _document.Redo(); Refresh(); });
        AddButton(history, "Revert to source", () => { _document.Revert(); Refresh(); });
        AddButton(layout, "Save project document", () =>
        {
            if (!Capture()) return;
            try
            {
                _document.Save(_data.Assets.Root, _data.ClientData, _source);
                _status.Text = "Saved to metadata/items. Reopen this item to reload it. Native tiledata is unchanged.";
            }
            catch (Exception e) { _status.Text = e.Message; }
        });
        _status = new Label
        {
            Text = "Edits stay in this document. Binary tiledata export is a separate milestone.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(550, 60),
        };
        layout.AddChild(_status);
        Refresh();
    }

    private static Button AddButton(BoxContainer parent, string title, Action action)
    {
        var button = new Button { Text = title, CustomMinimumSize = new Vector2(0, 40) };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    private bool Capture()
    {
        try
        {
            _document.Edit(_name.Text, ItemMetadataDocument.ParseFlags(_flags.Text), (int)_height.Value);
            Refresh();
            _status.Text = "Working document updated. Save to retain it after closing.";
            return true;
        }
        catch (Exception e) { _status.Text = e.Message; return false; }
    }

    private void Refresh()
    {
        _name.Text = _document.Current.Name;
        _flags.Text = $"0x{_document.Current.Flags:X16}";
        _height.Value = _document.Current.Height;
        _undo.Disabled = !_document.CanUndo;
        _redo.Disabled = !_document.CanRedo;
    }

    private void TryClose()
    {
        bool pending = _name.Text != _document.Current.Name
            || _flags.Text != $"0x{_document.Current.Flags:X16}" || (int)_height.Value != _document.Current.Height;
        if (!_document.IsDirty && !pending) { QueueFree(); return; }
        Show();
        var confirm = new ConfirmationDialog
        {
            Title = "Discard unsaved metadata?",
            DialogText = "The saved project document and native tiledata will remain unchanged.",
            OkButtonText = "Discard unsaved changes",
        };
        AddChild(confirm);
        confirm.Canceled += confirm.QueueFree;
        confirm.Confirmed += QueueFree;
        confirm.PopupCentered();
    }

    private void QueueClose()
    {
        if (_closeQueued) return;
        _closeQueued = true;
        // AcceptDialog may hide after its cancel signal. Ask on the next
        // frame so a canceled discard can reliably return to the editor.
        Callable.From(() =>
        {
            _closeQueued = false;
            if (GodotObject.IsInstanceValid(this)) TryClose();
        }).CallDeferred();
    }
}
#endif
