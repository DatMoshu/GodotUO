#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

public partial class AssetsView
{
    private Tree _library;
    private OptionButton _family;
    private InspectorDock _inspector;
    private Label _changeStatus;
    private MenuButton _changes;
    private Button _review;
    private bool _syncing;
    private readonly List<(TreeItem Item, AssetPanel Panel, bool? Land)> _libraryItems = new();
    private readonly List<Action> _changeActions = new();

    private void BuildWorkspace(HBoxContainer bar)
    {
        _family = new OptionButton { AccessibilityName = "Asset family", TooltipText = "Asset library · compact navigation" };
        _family.ItemSelected += i => { if (!_syncing && i < _panels.Count) ShowPanel(_panels[(int)i]); };
        bar.AddChild(_family);
        var workspace = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(workspace);
        _library = new Tree { HideRoot = true, CustomMinimumSize = new Vector2(148, 0) * EditorInterface.Singleton.GetEditorScale(), SizeFlagsVertical = SizeFlags.ExpandFill, AccessibilityName = "Asset library" };
        workspace.AddChild(_library); workspace.AddChild(_tabs);
        _library.ItemSelected += () =>
        {
            if (_syncing) return;
            foreach (var entry in _libraryItems)
                if (entry.Item == _library.GetSelected())
                {
                    ShowPanel(entry.Panel);
                    if (entry.Land.HasValue && entry.Panel is ArtPanel art)
                    { art.SelectKind(entry.Land.Value); art.Search(art.SearchInput.Text); }
                    _syncing = true; entry.Item.Select(0); _syncing = false;
                    break;
                }
        };
        Resized += FitLibrary;
        var tray = new HBoxContainer(); AddChild(tray);
        _changes = new MenuButton { Text = "Project changes", Icon = AssetUi.Icon("Filesystem"), TooltipText = "Review project image replacements and metadata drafts" };
        _changes.GetPopup().AboutToPopup += UpdateChanges;
        _changes.GetPopup().IdPressed += id => { if (id >= 0 && id < _changeActions.Count) _changeActions[(int)id](); };
        tray.AddChild(_changes);
        _changeStatus = new Label { Text = "Install read-only", ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill }; tray.AddChild(_changeStatus);
        _review = AssetUi.Action("FileDialog", "Select a project static image replacement to review its staged export", ReviewSelected);
        _review.Text = "Review export"; _review.Disabled = true; tray.AddChild(_review);
        Inspect += _ => UpdateChanges();
    }

    private void BuildLibrary()
    {
        TreeItem root = _library.CreateItem();
        TreeItem library = _library.CreateItem(root); library.SetText(0, "Library"); library.SetSelectable(0, false);
        TreeItem workflows = _library.CreateItem(root); workflows.SetText(0, "Workflows"); workflows.SetSelectable(0, false);
        foreach (AssetPanel panel in _panels)
        {
            string name = panel.Name;
            _family.AddItem(name);
            TreeItem item = _library.CreateItem(panel is BulkPanel or ArtSetPanel or ParityPanel ? workflows : library);
            item.SetText(0, name == "Anims" ? "Animations" : name);
            _libraryItems.Add((item, panel, null));
            if (panel is ArtPanel)
                foreach (bool land in new[] { false, true })
                {
                    TreeItem child = _library.CreateItem(item); child.SetText(0, land ? "Land" : "Statics");
                    _libraryItems.Add((child, panel, land));
                }
        }
        _tabs.TabChanged += _ => SyncLibrary();
        int saved = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata("guo_assets", "family", 0).AsInt32();
        _tabs.CurrentTab = Math.Clamp(saved, 0, _panels.Count - 1); SyncLibrary(); FitLibrary();
    }

    private void SyncLibrary()
    {
        if (_library == null || _syncing || _tabs.CurrentTab < 0 || _tabs.CurrentTab >= _panels.Count) return;
        _syncing = true;
        _family.Select(_tabs.CurrentTab);
        AssetPanel panel = _panels[_tabs.CurrentTab];
        foreach (var item in _libraryItems) if (item.Panel == panel && item.Land == null) { item.Item.Select(0); break; }
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata("guo_assets", "family", _tabs.CurrentTab);
        _syncing = false;
    }

    private void FitLibrary()
    {
        if (_library == null) return;
        bool compact = Size.X < 650 * EditorInterface.Singleton.GetEditorScale();
        _library.Visible = !compact; _family.Visible = compact;
    }

    public void AttachInspector(InspectorDock inspector)
    {
        _inspector = inspector;
        _inspector._Ready();
        _inspector.Metadata.Changed += UpdateChanges;
        _data.AssetsApplied += UpdateChanges;
        UpdateChanges();
    }

    public void FocusMetadata(int id)
    {
        MakeVisible();
        ArtPanel art = Panel<ArtPanel>();
        ShowPanel(art); art.SelectKind(false); art.Search($"0x{id:X4}");
        _inspector.Metadata.NameInput?.GrabFocus();
    }
    internal MetadataStageDialog OpenMetadataStage(int id)
    {
        if (_inspector.Metadata.CurrentDocument?.Id != id) FocusMetadata(id);
        return MetadataStageDialog.Open(_inspector.Metadata, _data, id);
    }
    internal void ReopenMetadata(int id) => _inspector.Metadata.Reopen(_data, id);

    private void ReviewSelected()
    {
        Inspection selected = _inspector?.Current;
        if (selected?.ArtKind == AssetKind.Static && _data.Assets?.Has(AssetKind.Static, selected.ArtId) == true)
            StaticStageDialog.Open(_data, selected.ArtId, selected.Image);
    }

    private void UpdateChanges()
    {
        if (_changes == null || !IsInstanceValid(this) || _data.Assets == null) return;
        PopupMenu menu = _changes.GetPopup(); menu.Clear(); _changeActions.Clear();
        int images = 0;
        foreach (AssetKind kind in Enum.GetValues<AssetKind>())
            foreach (int id in _data.Assets.Ids(kind))
            {
                images++;
                AssetKind assetKind = kind; int assetId = id;
                AddChange($"{kind} 0x{id:X4} · Project image", () =>
                {
                    if (assetKind is AssetKind.Static or AssetKind.Land)
                    { ArtPanel art = Panel<ArtPanel>(); ShowPanel(art); art.SelectKind(assetKind == AssetKind.Land); art.Search($"0x{assetId:X4}"); }
                    else if (assetKind == AssetKind.Gump) { ShowPanel(Panel<GumpPanel>()); Panel<GumpPanel>().Search($"0x{assetId:X4}"); }
                    else if (assetKind == AssetKind.Hue) { ShowPanel(Panel<HuePanel>()); Panel<HuePanel>().Search(assetId.ToString()); }
                    else if (assetKind == AssetKind.Animation) { ShowPanel(Panel<AnimationPanel>()); Panel<AnimationPanel>().Search(assetId.ToString()); }
                });
            }
        var metadata = new Dictionary<int, string>();
        string directory = Path.Combine(_data.Assets.Root, "metadata", "items");
        if (Directory.Exists(directory))
            foreach (string file in Directory.EnumerateFiles(directory, "0x*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(file).AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int id) && id < 0x10000)
                    metadata[id] = "Saved metadata document";
        if (_inspector?.Metadata != null) foreach (var entry in _inspector.Metadata.Changes(_data.Assets.Root)) metadata[entry.Id] = entry.State;
        if (metadata.Count > 0) menu.AddSeparator("Metadata · project documents only");
        foreach (var entry in metadata)
        { int id = entry.Key; AddChange($"0x{id:X4} · {entry.Value}", () => FocusMetadata(id)); }
        TexturePanel texturePanel = Panel<TexturePanel>(); var textures = texturePanel?.Changes() ?? new();
        if (textures.Count > 0) menu.AddSeparator("Textures · project documents");
        foreach (var entry in textures)
        {
            int id = entry.Key; AddChange($"Texture 0x{id:X4} · {entry.Value}", () => { ShowPanel(texturePanel); texturePanel.Search($"0x{id:X4}"); });
        }
        _changes.Disabled = images + metadata.Count + textures.Count == 0;
        _changeStatus.Text = (textures.Count > 0 ? $"{textures.Count} texture document(s) · " : "") + $"{images} image replacement(s) · {metadata.Count} metadata document(s)";
        Inspection selected = _inspector?.Current;
        _review.Disabled = selected?.ArtKind != AssetKind.Static || !_data.Assets.Has(AssetKind.Static, selected.ArtId);
        _review.TooltipText = _review.Disabled ? "Select a project static image replacement. Metadata documents are not included in static export." : "Compare the selected static and prepare its verified staged export";
    }

    private void AddChange(string text, Action run)
    { _changes.GetPopup().AddItem(text, _changeActions.Count); _changeActions.Add(run); }
}
#endif
