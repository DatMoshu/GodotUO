#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.UI.Authoring;

public partial class GumpStudio
{
    private void ConfirmDiscard(Action action)
    {
        if (!Dirty) { action(); return; }
        var dialog = new ConfirmationDialog { DialogText = "Discard unsaved changes to this document?", Title = "Unsaved gump" };
        AddChild(dialog); dialog.Confirmed += () => { dialog.Hide(); action(); dialog.QueueFree(); }; dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }
    private void FileDialog(string title, bool save, string filter, Action<string> selected)
    {
        var dialog = new Godot.FileDialog
        {
            Title = title, Access = Godot.FileDialog.AccessEnum.Filesystem,
            FileMode = save ? Godot.FileDialog.FileModeEnum.SaveFile : Godot.FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { filter }, CurrentDir = Directory.Exists(Folder) ? Folder : EditorData.RepoRoot,
            Size = new Vector2I(900, 650)
        };
        AddChild(dialog);
        dialog.FileSelected += path => { Guard(() => selected(path)); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }
    private void OpenDialog() => FileDialog("Open gump document or captured client gump", false, "*.json ; Gump document", path =>
    {
        string content = File.ReadAllText(path);
        using var json = JsonDocument.Parse(content);
        bool bundle = json.RootElement.TryGetProperty("layout", out var layout);
        var doc = bundle ? ClassicGumpCodec.Import(layout.GetString(), json.RootElement.GetProperty("texts").EnumerateArray().Select(t => t.GetString())) : GumpDocument.Parse(content);
        if (bundle) ResolveImportedArtSizes(doc);
        LoadDocument(doc); _path = bundle ? "" : path; _saved = bundle ? "" : _document.ToJson(); Refresh();
        SetStatus($"Opened {Path.GetFileName(path)}");
    });
    private void Save(bool saveAs)
    {
        if (saveAs || _path.Length == 0) FileDialog("Save gump document", true, "*.gump.json ; Gump document", SaveTo);
        else Guard(() => SaveTo(_path));
    }
    public void SaveTo(string path)
    {
        _document.Validate();
        WriteSafe(path, _document.ToJson()); _path = path; _saved = _document.ToJson(); Refresh();
        SetStatus($"Saved {Path.GetFileName(path)}");
    }
    private static void WriteSafe(string path, string content)
    {
        path = Path.GetFullPath(path);
        string install = EditorData.Setting("UO_CLIENT_DATA", "");
        if (!string.IsNullOrEmpty(install))
        {
            string root = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Save into your world project, not the UO client install.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }
    private void ImportDialog()
    {
        var dialog = new ConfirmationDialog { Title = "Import Classic layout + text table", Size = new Vector2I(880, 680), OkButtonText = "Import", DialogHideOnOk = false };
        AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(new Label { Text = "Paste brace-delimited UO commands. Unknown commands are preserved." });
        var layout = new TextEdit { CustomMinimumSize = new Vector2(800, 330), SizeFlagsVertical = SizeFlags.ExpandFill, Text = "{ page 0 }\n{ resizepic 0 0 5054 480 320 }\n{ text 24 24 0 0 }\n{ button 24 260 247 248 1 0 1 }" }; box.AddChild(layout);
        box.AddChild(new Label { Text = "Text table as a JSON array of strings (order is significant)" });
        var texts = new TextEdit { CustomMinimumSize = new Vector2(800, 120), Text = "[\"Welcome to Britannia\"]" }; box.AddChild(texts);
        dialog.Confirmed += () => Guard(() =>
        {
            var doc = ClassicGumpCodec.Import(layout.Text, JsonSerializer.Deserialize<string[]>(texts.Text) ?? Array.Empty<string>());
            ResolveImportedArtSizes(doc);
            dialog.Hide();
            ConfirmDiscard(() => { LoadDocument(doc); _path = ""; SetStatus($"Imported {doc.Elements.Count} elements; {doc.Notes.Count} notices in Document properties."); });
            dialog.QueueFree();
        });
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }
    private void ExportDialog()
    {
        var dialog = new AcceptDialog { Title = "Export gump", Size = new Vector2I(500, 260) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(new Label { Text = "Classic: layout + text table for your shard.\nModern: Godot scene with document and reply events.\nNeither export replaces a built-in client's C# behavior.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddButton(box, "Classic layout bundle…", () =>
        {
            dialog.Hide();
            FileDialog("Export classic bundle", true, "*.classic.json ; Classic layout and texts", path =>
            {
                var result = ClassicGumpCodec.Export(_document);
                WriteSafe(path, JsonSerializer.Serialize(new { layout = result.Layout, texts = result.Texts }, new JsonSerializerOptions { WriteIndented = true }));
                SetStatus("Exported classic bundle. Pass layout and texts to the shard's gump API.");
            }); dialog.QueueFree();
        });
        AddButton(box, "Godot scene (.tscn)…", () =>
        {
            dialog.Hide();
            FileDialog("Export Godot scene", true, "*.tscn ; Godot scene", path =>
            {
                _document.Validate();
                // Serialize the document as a Godot string literal, with its script in the same project.
                WriteSafe(path, SceneText(_document)); SetStatus("Exported scene. Connect AuthoredGumpView.Reply to your game's action handler.");
            }); dialog.QueueFree();
        });
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    internal static string SceneText(GumpDocument doc)
    {
        doc.Validate();
        return "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://src/UI/Authoring/AuthoredGumpView.cs\" id=\"1\"]\n\n[node name=\"AuthoredGump\" type=\"Control\"]\nlayout_mode = 0\noffset_right = " + doc.Width + ".0\noffset_bottom = " + doc.Height + ".0\nscript = ExtResource(\"1\")\nDocumentJson = " + GD.VarToStr(doc.ToJson()) + "\n";
    }

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.CtrlPressed && k.Keycode == Key.S) { Save(k.ShiftPressed); GetViewport().SetInputAsHandled(); return; }
        var focus = GetViewport().GuiGetFocusOwner();
        if (focus is LineEdit or TextEdit || _preview.ButtonPressed) return;
        if (k.CtrlPressed && k.Keycode == Key.Z) { if (k.ShiftPressed) Redo(); else Undo(); }
        else if (k.CtrlPressed && k.Keycode == Key.Y) Redo();
        else if (k.CtrlPressed && k.Keycode == Key.D) Duplicate();
        else if (k.Keycode == Key.Delete) Delete();
        else if (k.Keycode is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int step = k.ShiftPressed ? 8 : 1;
            Mutate(() => { foreach (var e in Selected().Where(e => !e.Locked)) { e.X += k.Keycode == Key.Left ? -step : k.Keycode == Key.Right ? step : 0; e.Y += k.Keycode == Key.Up ? -step : k.Keycode == Key.Down ? step : 0; } });
        }
        else return;
        GetViewport().SetInputAsHandled();
    }
}
#endif
