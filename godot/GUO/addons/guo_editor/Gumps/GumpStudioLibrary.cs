#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

public partial class GumpStudio
{
    private sealed record LibraryEntry(string Name, string Kind, string Path = "");
    internal void ClientGumpDialog()
    {
        var dialog = new AcceptDialog { Title = "Open client gump", Size = new Vector2I(780, 580), OkButtonText = "Open selected", DialogHideOnOk = false };
        AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(new Label { Text = "Double-click a preview or capture to load it onto the canvas.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var search = new LineEdit { PlaceholderText = "Search gumps by name…", ClearButtonEnabled = true }; box.AddChild(search);
        var showUnavailable = new CheckBox { Text = "Also show gumps requiring a live capture" }; box.AddChild(showUnavailable);
        var list = new ItemList { CustomMinimumSize = new Vector2(640, 300), SizeFlagsVertical = SizeFlags.ExpandFill }; box.AddChild(list);
        var details = new Label { Text = "Preview uses the editor's offline character. Captures preserve your actual game state.", AutowrapMode = TextServer.AutowrapMode.WordSmart }; box.AddChild(details);
        var entries = new List<LibraryEntry>();
        void Populate()
        {
            entries.Clear(); list.Clear();
            var choices = ClientGumpLibrary.Factories.Keys.Select(n => new LibraryEntry(n, "Preview")).ToList();
            if (Directory.Exists(GumpCapture.DirectoryPath))
                choices.AddRange(Directory.EnumerateFiles(GumpCapture.DirectoryPath, "*.gump.json")
                    .Select(f => new LibraryEntry(Path.GetFileName(f)[..^10], "Capture", f)));
            if (showUnavailable.ButtonPressed)
            {
                string sourceRoot = ProjectSettings.GlobalizePath("res://src/Game/UI/Gumps");
                choices.AddRange(Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                    .Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !ClientGumpLibrary.Factories.ContainsKey(n) && !n.Contains('.'))
                    .Select(n => new LibraryEntry(n, "Needs capture")));
            }
            foreach (var entry in choices.Where(e => e.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Kind).ThenBy(e => e.Name))
            {
                entries.Add(entry); list.AddItem($"{entry.Name}    ·    {entry.Kind}");
            }
            if (entries.Count > 0) list.Select(0);
        }
        void OpenEntry(int index)
        {
            if (index < 0 || index >= entries.Count) return;
            var entry = entries[index];
            if (entry.Kind == "Needs capture")
            {
                details.Text = "Open this gump in the client and type -gumpcapture. Then press Refresh captures here.\nCapture directory: " + GumpCapture.DirectoryPath;
                return;
            }
            try
            {
                var doc = entry.Kind == "Capture" ? GumpDocument.Parse(File.ReadAllText(entry.Path)) : ClientGumpLibrary.Preview(entry.Name, PreviewWorld?.Invoke());
                dialog.Hide();
                ConfirmDiscard(() =>
                {
                    LoadDocument(doc); _path = entry.Kind == "Capture" ? entry.Path : ""; _saved = _document.ToJson();
                    _canvas.Page = 1; _page.SetValueNoSignal(1); Refresh();
                    SetStatus($"Opened {entry.Name} · {doc.Elements.Count} controls · {entry.Kind.ToLowerInvariant()}");
                });
                dialog.QueueFree();
            }
            catch (Exception ex) { details.Text = $"Could not open {entry.Name}: {ex.Message}"; }
        }
        list.ItemActivated += i => OpenEntry((int)i);
        dialog.Confirmed += () => OpenEntry(list.GetSelectedItems().FirstOrDefault(-1));
        dialog.Canceled += dialog.QueueFree;
        search.TextChanged += _ => Populate(); showUnavailable.Toggled += _ => Populate();
        AddButton(box, "Refresh captures", Populate);
        AddButton(box, "Open document file…", () => { dialog.Hide(); ConfirmDiscard(OpenDialog); dialog.QueueFree(); });
        Populate(); dialog.PopupCentered(); search.GrabFocus();
    }
}
#endif
