#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

/// <summary>UO UI authoring workspace. Edits are independent documents, never writes to the install.</summary>
[Tool]
public partial class GumpStudio : VBoxContainer
{
    private EditorData _data;
    private GumpDocument _document = new();
    private readonly GumpHistory _history = new();
    private readonly Dictionary<int, Texture2D> _textures = new();
    private GumpCanvas _canvas;
    private ItemList _layers;
    private VBoxContainer _properties;
    private Label _status, _title;
    private Control _extent;
    private CheckButton _preview;
    private SpinBox _page;
    private float _zoom = 1;
    private string _path = "", _saved = "";
    private bool _refreshing;
    private AssetsView _gumpAssets;
    private AssetField _quickAdd;
    private ScrollContainer _inspectorScroll;
    private HSplitContainer _rightSplit;
    private float UiScale => EditorInterface.Singleton.GetEditorScale();
    internal AssetsView GumpAssets => _gumpAssets;
    internal AssetField QuickAddField => _quickAdd;
    internal ScrollContainer PropertiesScroll => _inspectorScroll;
    internal Func<GUO.Game.World> PreviewWorld { get; set; }
    public GumpDocument Document => _document;
    public GumpCanvas Canvas => _canvas;
    public bool Dirty => _saved != _document.ToJson();
    public GumpStudio() { }
    public GumpStudio(EditorData data) { _data = data; }
    private string Folder => Path.Combine(EditorData.ProjectRoot(), "gumps");

    public override void _Ready()
    {
        Name = "UOGumps";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SizeFlagsHorizontal = SizeFlagsVertical = SizeFlags.ExpandFill;
        TextureFilter = TextureFilterEnum.Nearest;
        var toolbar = new HFlowContainer(); AddChild(toolbar);
        AddButton(toolbar, "New Classic", () => ConfirmDiscard(() => NewDocument(false)));
        AddButton(toolbar, "New Modern", () => ConfirmDiscard(() => NewDocument(true)));
        AddButton(toolbar, "Open…", () => ConfirmDiscard(OpenDialog));
        AddButton(toolbar, "Save", () => Save(false));
        AddButton(toolbar, "Save As…", () => Save(true));
        AddButton(toolbar, "Import layout…", ImportDialog);
        AddButton(toolbar, "Export…", ExportDialog);
        AddButton(toolbar, "Undo", Undo);
        AddButton(toolbar, "Redo", Redo);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, ClipText = true, CustomMinimumSize = new Vector2(120, 0) }; toolbar.AddChild(_title);

        var options = new HFlowContainer(); AddChild(options);
        options.AddChild(new Label { Text = "Page" });
        _page = new SpinBox { MinValue = 0, MaxValue = 65535, Value = 1 }; options.AddChild(_page);
        _page.ValueChanged += v => { _canvas.Page = (int)v; Refresh(); };
        var snap = new CheckButton { Text = "Snap 8 px", ButtonPressed = true }; options.AddChild(snap);
        snap.Toggled += v => _canvas.Snap = v;
        options.AddChild(new Label { Text = "Zoom" });
        var zoom = new OptionButton(); foreach (string z in new[] { "50%", "75%", "100%", "150%", "200%" }) zoom.AddItem(z);
        zoom.Selected = 2; options.AddChild(zoom);
        zoom.ItemSelected += i => { _zoom = new[] { .5f, .75f, 1f, 1.5f, 2f }[i]; RefreshCanvas(); };
        AddButton(options, "Duplicate", Duplicate);
        AddButton(options, "Delete", Delete);
        AddButton(options, "Raise", () => Reorder(1));
        AddButton(options, "Lower", () => Reorder(-1));
        AddButton(options, "Align left", () => Align(false));
        AddButton(options, "Align top", () => Align(true));
        _preview = new CheckButton { Text = "Interact" }; options.AddChild(_preview);
        _preview.Toggled += _ => RefreshCanvas();

        var work = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(work);
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffsets = new[] { (int)(200 * UiScale) }, CustomMinimumSize = new Vector2(0, 260) }; work.AddChild(split);
        var libraryColumn = new VBoxContainer { CustomMinimumSize = new Vector2(210 * UiScale, 0) }; split.AddChild(libraryColumn);
        var libraryScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; libraryColumn.AddChild(libraryScroll);
        var library = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; libraryScroll.AddChild(library);
        library.AddChild(new Label { Text = "Add gump art" });
        _quickAdd = new AssetField(_data, AssetPickKind.Gump)
        {
            QuickAdd = true,
            Placeholder = "name or id, Enter adds",
            TooltipText = "Type a gump's name (paperdoll, button, stone) or its id (0x0BB8, 3000, bb8).\nEnter adds it and keeps the box ready for the next one. The search button browses all gumps.",
        };
        _quickAdd.Committed += id => Guard(() => AddArt(id));
        library.AddChild(_quickAdd);
        library.AddChild(new HSeparator());
        library.AddChild(new Label { Text = "ELEMENTS" });
        var palette = new OptionButton();
        foreach (var kind in Enum.GetValues<GumpElementKind>()) palette.AddItem(kind.ToString());
        library.AddChild(palette);
        AddButton(library, "+ Add element", () => AddElement((GumpElementKind)palette.Selected));
        library.AddChild(new HSeparator());
        library.AddChild(new Label { Text = "LAYERS · Shift-click to select" });
        _layers = new ItemList { SelectMode = ItemList.SelectModeEnum.Multi, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(190, 160) };
        library.AddChild(_layers);
        _layers.MultiSelected += (_, _) =>
        {
            if (_refreshing) return;
            _canvas.Selection.Clear();
            foreach (int index in _layers.GetSelectedItems()) _canvas.Selection.Add(_document.Elements[index].Id);
            _canvas.RefreshSelection(); RefreshProperties();
        };
        AddButton(libraryColumn, "Document properties", () => { _canvas.Selection.Clear(); Refresh(); });
        AddButton(libraryColumn, "Load client gump…", ClientGumpDialog);

        var right = _rightSplit = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; split.AddChild(right);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; right.AddChild(scroll);
        _extent = new Control(); scroll.AddChild(_extent);
        _canvas = new GumpCanvas(); _extent.AddChild(_canvas);
        _canvas.SelectionChanged += () => { RefreshLayers(); RefreshProperties(); };
        _canvas.BeginEdit += () => _history.Push(_document);
        _canvas.EndEdit += Refresh;
        _canvas.ArtDropped += (id, at) => Guard(() => AddArt(id, at));
        _canvas.Replied += r => SetStatus($"Reply {r.ButtonId} · switches [{string.Join(", ", r.Switches)}] · entries {string.Join(", ", r.Entries.Select(t => $"{t.Item1}={t.Item2}"))}");
        _inspectorScroll = new ScrollContainer { CustomMinimumSize = new Vector2(310 * UiScale, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        right.AddChild(_inspectorScroll);
        _properties = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _inspectorScroll.AddChild(_properties);
        right.Resized += FitInspector;
        BuildAssetShelf(work);
        work.Resized += () => work.SplitOffsets = new[] { Math.Max(200, (int)(work.Size.Y - 225 * UiScale)) };
        Callable.From(FitInspector).CallDeferred();
        _status = new Label { Text = "Ready", AutowrapMode = TextServer.AutowrapMode.WordSmart }; AddChild(_status);
        if (_data != null) _data.Loaded += OnDataLoaded;
        NewDocument(true);
        string recovery = Path.Combine(Folder, ".recovery.json");
        if (File.Exists(recovery)) Guard(() => { LoadDocument(GumpDocument.Parse(File.ReadAllText(recovery))); SetStatus("Recovered the previous workspace. Save As to keep it."); });
    }

    private void OnDataLoaded() { _textures.Clear(); RefreshCanvas(); }
    public void Shutdown()
    {
        if (_data != null) _data.Loaded -= OnDataLoaded;
        if (_canvas != null) Guard(() => WriteSafe(Path.Combine(Folder, ".recovery.json"), _document.ToJson()));
        _textures.Clear();
    }
    private Texture2D Texture(int id)
    {
        if (_textures.TryGetValue(id, out var t)) return t;
        using var image = _data?.GumpImage(id);
        if (image == null) return null;
        return _textures[id] = ImageTexture.CreateFromImage(image);
    }
    public void LoadDocument(GumpDocument doc)
    {
        doc.Validate(); _history.Push(_document); _document = doc;
        _canvas.Selection.Clear(); Refresh();
    }
    private void ResolveImportedArtSizes(GumpDocument doc)
    {
        foreach (var e in doc.Elements.Where(e => e.Kind is GumpElementKind.Image or GumpElementKind.Button or GumpElementKind.CheckBox or GumpElementKind.Radio))
        {
            var texture = Texture(e.Graphic);
            if (texture == null) continue;
            e.Width = texture.GetWidth(); e.Height = texture.GetHeight();
        }
    }
    private void NewDocument(bool modern)
    {
        var doc = new GumpDocument { Modern = modern, Name = modern ? "Modern gump" : "Classic gump" };
        doc.Elements.Add(new GumpElement { Kind = GumpElementKind.Panel, Name = "Window", X = 0, Y = 0, Width = 640, Height = 480, Graphic = modern ? 0 : 5054, Anchor = "Stretch", Locked = true });
        doc.Elements.Add(new GumpElement { Kind = GumpElementKind.Label, Name = "Heading", X = 32, Y = 24, Width = 480, Height = 40, Text = "A new adventure", FontSize = 28 });
        doc.Elements.Add(new GumpElement { Kind = GumpElementKind.Button, Name = "Continue", X = 448, Y = 400, Width = 160, Height = 48, Text = "Continue", Graphic = modern ? 0 : 247, GraphicDown = modern ? 0 : 248, Anchor = "BottomRight" });
        LoadDocument(doc); _path = ""; _saved = doc.ToJson(); Refresh();
    }
    /// <summary>
    /// Adds gump art as an image. Dropped art lands where it was dropped; typed
    /// or double-clicked art lands just below and right of the selection, so a
    /// run of quick adds fans out instead of stacking at the corner.
    /// </summary>
    public void AddArt(int id, Vector2? at = null)
    {
        var texture = Texture(id) ?? throw new InvalidDataException($"No local gump art at 0x{id:X4}; wait for the client data to load.");
        int grid = _canvas.Snap ? Math.Max(1, _canvas.Grid) : 1;
        var anchor = Selected().LastOrDefault();
        Vector2 spot = at ?? (anchor != null && !anchor.Locked ? new Vector2(anchor.X + 16, anchor.Y + 16) : new Vector2(16, 16));
        int x = (int)Math.Round(spot.X / grid) * grid, y = (int)Math.Round(spot.Y / grid) * grid;
        Mutate(() =>
        {
            var e = new GumpElement { Kind = GumpElementKind.Image, Name = $"Art 0x{id:X4}", Graphic = id, X = x, Y = y, Width = texture.GetWidth(), Height = texture.GetHeight(), Page = _canvas.Page };
            _document.Elements.Add(e); Select(e);
        });
        SetStatus($"Added gump 0x{id:X4} at {x}, {y}");
    }
    private void AddElement(GumpElementKind kind) => Mutate(() =>
    {
        var e = new GumpElement { Kind = kind, Name = kind.ToString(), Text = kind is GumpElementKind.Image or GumpElementKind.Panel ? "" : kind.ToString(), Page = _canvas.Page,
            ReplyId = _document.Elements.Select(e => e.ReplyId).DefaultIfEmpty(0).Max() + 1,
            EntryId = _document.Elements.Select(e => e.EntryId).DefaultIfEmpty(0).Max() + 1 };
        _document.Elements.Add(e); Select(e);
    });
    private void Select(GumpElement e) { _canvas.Selection.Clear(); _canvas.Selection.Add(e.Id); }
    private IEnumerable<GumpElement> Selected() => _document.Elements.Where(e => _canvas.Selection.Contains(e.Id));
    public void Undo() { _document = _history.Undo(_document); Refresh(); }
    public void Redo() { _document = _history.Redo(_document); Refresh(); }
    private void Mutate(Action action) { _history.Push(_document); action(); Refresh(); }
    private void Delete() => Mutate(() => _document.Elements.RemoveAll(e => _canvas.Selection.Contains(e.Id) && !e.Locked));
    private void Duplicate() => Mutate(() =>
    {
        var copies = _document.Clone().Elements.Where(e => _canvas.Selection.Contains(e.Id)).ToList();
        _canvas.Selection.Clear();
        foreach (var e in copies) { e.Id = Guid.NewGuid().ToString("N"); e.Name += " copy"; e.X += 16; e.Y += 16; _document.Elements.Add(e); _canvas.Selection.Add(e.Id); }
    });
    private void Reorder(int direction) => Mutate(() =>
    {
        var items = Selected().Where(e => !e.Locked).ToList();
        if (direction > 0) items.Reverse();
        foreach (var e in items)
        {
            int old = _document.Elements.IndexOf(e), next = Math.Clamp(old + direction, 0, _document.Elements.Count - 1);
            _document.Elements.RemoveAt(old); _document.Elements.Insert(next, e);
        }
    });
    private void Align(bool vertical) => Mutate(() =>
    {
        var selection = Selected().Where(e => !e.Locked).ToArray(); if (selection.Length == 0) return;
        int at = selection.Min(e => vertical ? e.Y : e.X);
        foreach (var e in selection) { if (vertical) e.Y = at; else e.X = at; }
    });
    private void Refresh()
    {
        if (_canvas == null) return;
        _canvas.Selection.RemoveWhere(id => !_document.Elements.Any(e => e.Id == id));
        _title.Text = _document.Name + (Dirty ? " *" : "");
        RefreshCanvas(); RefreshLayers(); RefreshProperties();
    }
    private void RefreshCanvas()
    {
        if (_canvas == null) return;
        _canvas.Document = _document; _canvas.Scale = new Vector2(_zoom, _zoom);
        _extent.CustomMinimumSize = new Vector2(_document.Width, _document.Height) * _zoom;
        _canvas.Rebuild(Texture, _preview.ButtonPressed);
    }
    private void RefreshLayers()
    {
        _refreshing = true; _layers.Clear();
        foreach (var e in _document.Elements)
        {
            int index = _layers.AddItem($"{(e.Locked ? "[L] " : "")}{e.Name} · p{e.Page}{(!e.Visible ? " (hidden)" : "")}");
            if (_canvas.Selection.Contains(e.Id)) _layers.Select(index, false);
        }
        _refreshing = false;
    }
    private void SetStatus(string text) => _status.Text = text;
    private void Guard(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message); GD.PrintErr($"[Gump Studio] {ex.Message}"); } }
    private static void AddButton(Node parent, string text, Action action)
    {
        var b = new Button { Text = text }; parent.AddChild(b); b.Pressed += action;
    }
}
#endif
