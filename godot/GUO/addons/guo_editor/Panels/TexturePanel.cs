#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Godot;

/// <summary>Sloped-land textures, decoded by the game's loader with effective TexTerr.def remaps.</summary>
[Tool]
public partial class TexturePanel : GridPanel
{
    private List<int> _ids;
    private Dictionary<int, List<(int Id, string Name)>> _land;
    private OptionButton _size;
    private readonly Dictionary<string, TextureDocument> _documents = new();
    private readonly Dictionary<string, string> _errors = new();
    private bool _showSource;
    internal event Action<TextureDocument> DocumentReopening;
    private string Project => Data.Assets?.Root ?? EditorData.ProjectRoot();
    private string Key(int id) => Project + "|" + id;
    public override string SmokeQuery => "0x0003";
    protected override string Placeholder => "Texture ID (0x0001, 1) or referring land name — Enter";
    protected override int IconSize => 64;
    protected override int PageSize => 120;

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _size = new OptionButton { AccessibilityName = "Texture dimensions", TooltipText = "Filter native texture dimensions" };
        foreach (string name in new[] { "All sizes", "64 × 64", "128 × 128" }) _size.AddItem(name);
        _size.ItemSelected += _ => Refresh(); bar.AddChild(_size);
        var source = new CheckButton { Text = "Source", TooltipText = "Compare the installed source texture with the working copy", AccessibilityName = "Show source texture" };
        source.Toggled += value => { _showSource = value; Reinspect(); }; bar.AddChild(source);
        bar.AddChild(AssetUi.Action("Reload", "Reopen texture and discard its unsaved draft…", () =>
        {
            if (Selected is not int id) return;
            var dialog = new ConfirmationDialog { Title = "Reopen texture?", DialogText = "Discard unsaved texture edits and reopen the saved project copy?", OkButtonText = "Reopen texture" };
            dialog.Canceled += dialog.QueueFree;
            dialog.Confirmed += () => { if (_documents.TryGetValue(Key(id), out var doc)) DocumentReopening?.Invoke(doc); _documents.Remove(Key(id)); _errors.Remove(Key(id)); dialog.QueueFree(); OnAssetsChanged(); };
            AddChild(dialog); dialog.PopupCentered();
        }));
    }
    internal TextureDocument WorkingDocument(int id)
    {
        if (_documents.TryGetValue(Key(id), out var document)) return document;
        if (_errors.ContainsKey(Key(id))) return null;
        try { document = TextureDocument.Open(Project, Data.ClientData, id, () => Data.SourceTexture(id)); _documents[Key(id)] = document; return document; }
        catch (Exception e) { _errors[Key(id)] = e.Message; return null; }
    }
    internal bool IsCurrentProject(TextureDocument document) => document.Project == EditorPaths.Real(Project);
    internal Dictionary<int, string> Changes()
    {
        var changes = new Dictionary<int, string>();
        string directory = EditorPaths.ProjectFile(Project, Data.ClientData, "metadata", "textures");
        if (Directory.Exists(directory))
            foreach (string file in Directory.EnumerateFiles(directory, "0x*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(file).AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int id) && id >= 0 && id < 0x4000)
                    changes[id] = "Saved texture document";
        foreach (TextureDocument document in _documents.Values)
            if (IsCurrentProject(document) && document.IsDirty) changes[document.Id] = "Unsaved texture document";
        return changes;
    }
    internal void FilterSize(int size)
    {
        EnsureUi(); _size.Select(size switch { 64 => 1, 128 => 2, _ => 0 }); Refresh();
    }
    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
            _ids = Enumerable.Range(0, Math.Min(Data.Files.Texmaps.File.Entries.Length,
                GUO.Assets.TexmapsLoader.MAX_LAND_TEXTURES_DATA_INDEX_COUNT)).Where(id => Data.TextureSize(id) > 0).ToList();
        return _ids;
    }
    protected override void ForgetIds() { _ids = null; _land = null; }
    protected override bool Include(int id) => _size.Selected == 0 || Data.TextureSize(id) == (_size.Selected == 1 ? 64 : 128);
    protected override string Caption(int id) => $"{id:X4}";
    private List<(int Id, string Name)> Land(int id)
    {
        if (_land == null)
        {
            _land = new(); var tiles = Data.Files.TileData.LandData;
            for (int i = 0; i < tiles.Length; i++)
            {
                int texture = tiles[i].TexID;
                if (!_land.TryGetValue(texture, out var uses)) _land[texture] = uses = new();
                uses.Add((i, tiles[i].Name ?? ""));
            }
        }
        return _land.TryGetValue(id, out var names) ? names : new();
    }
    internal (int Id, string Name)[] LandReferences(int id) => Land(id).ToArray();
    protected override bool Matches(int id, string query) => Land(id).Any(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    protected override string Tooltip(int id) => $"Texture 0x{id:X4} · {Data.TextureSize(id)} × {Data.TextureSize(id)}\n"
        + string.Join(", ", Land(id).Select(t => t.Name).Where(n => n.Length > 0).Distinct().Take(8));
    protected override Image Icon(int id)
    {
        if (!_documents.ContainsKey(Key(id)) && !File.Exists(EditorPaths.ProjectFile(Project, Data.ClientData, "metadata", "textures", $"0x{id:X4}.json"))) return Data.TextureImage(id);
        TextureDocument document = WorkingDocument(id); return document == null ? Data.TextureImage(id) : TextureDocument.Image(document.Record());
    }
    protected override Inspection Describe(int id)
    {
        TextureDocument document = WorkingDocument(id);
        Image image = document == null ? Data.TextureImage(id) : TextureDocument.Image(_showSource ? document.SourceRecord() : document.Record()); var uses = Land(id);
        var inspection = Inspection.Still("Textures", $"0x{id:X4}", image,
            $"[b]Texture 0x{id:X4}[/b] ({id})\nsize {Data.TextureSize(id)} × {Data.TextureSize(id)}\n"
            + $"{uses.Count} land tile reference(s)\n" + string.Join("\n", uses.Take(16).Select(t => $"0x{t.Id:X4} · {t.Name}"))
            + (uses.Count > 16 ? $"\n… {uses.Count - 16} more" : ""));
        inspection.Title = "Texture"; inspection.Origin = _showSource ? "Source comparison · read-only" : document?.IsDirty == true ? "Working texture · unsaved"
            : document != null && File.Exists(document.DocumentPath) ? "Saved project texture · native files unchanged" : "Source texture · native files read-only";
        inspection.Fields.Add(("Dimensions", image == null ? "No image" : $"{image.GetWidth()} × {image.GetHeight()}"));
        inspection.Fields.Add(("Land references", uses.Count.ToString()));
        inspection.Fields.Add(("Land tiles", uses.Count == 0 ? "None" : string.Join("\n", uses.Take(16).Select(t => $"0x{t.Id:X4} · {t.Name}"))
            + (uses.Count > 16 ? $"\n… {uses.Count - 16} more" : "")));
        inspection.Fields.Add(("Source", "texmaps.mul / texidx.mul"));
        if (image != null) inspection.Actions.Add(new("export-texture-png", "Export texture PNG…", "Save", () =>
            AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, $"texture_0x{id:X4}.png", path => AssetActions.ExportPng(image, path),
                owner: this, title: "Export texture PNG")));
        if (document != null)
        {
            inspection.Texture = document; inspection.TextureChanged = OnAssetsChanged;
            inspection.Actions.Add(new("replace-texture-png", "Replace texture from PNG…", "Load", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.OpenFile, null,
                path => OpenImportReview(document, TextureDocument.Read(path, 2 * 1024 * 1024), Path.GetFileName(path)), owner: this, title: "Review texture replacement")));
            inspection.Actions.Add(new("export-texture-stage", "Export verified texture stage…", "Save", () => OpenNativeStage(document)));
        }
        else inspection.Fields.Add(("Document refused", _errors.GetValueOrDefault(Key(id)) ?? "Source unavailable"));
        return inspection;
    }
    internal TextureImportDialog OpenImportReview(TextureDocument document, byte[] png, string name) => TextureImportDialog.Open(this, document, png, name);
    internal TextureStageDialog OpenNativeStage(TextureDocument document) => TextureStageDialog.Open(this, Data, document);
}
#endif
