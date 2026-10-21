#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>Shared asset identity, pixel preview, commands and document-backed properties.</summary>
[Tool]
public partial class InspectorDock : EditorDock
{
    private VBoxContainer _content;
    private TextureRect _preview;
    private OptionButton _zoom;
    private HBoxContainer _player, _actions;
    private Control _previewArea, _previewControls;
    private Button _play;
    private HSlider _frame;
    private Label _title, _origin, _dimensions, _history;
    private RichTextLabel _fields;
    private GridContainer _sourceFields;
    private TabContainer _pages;
    private VBoxContainer _properties, _imagePage;
    private bool _playing;
    private double _clock;

    public Inspection Current { get; private set; }
    public Texture2D Texture => _preview?.Texture;
    public ItemMetadataEditor Metadata { get; private set; }
    public ClilocEditor Strings { get; private set; }
    public SoundEditor Sound { get; private set; }
    public HueEditor Hues { get; private set; }
    public TextureEditor Textures { get; private set; }
    internal Control TourFields => _fields;
    internal HBoxContainer ActionToolbar => _actions;

    public InspectorDock()
    {
        Name = "UOInspector"; Title = "UO Inspector"; LayoutKey = "guo_inspector";
        DefaultSlot = DockSlot.RightUl; AvailableLayouts = DockLayout.Vertical | DockLayout.Floating; IconName = "Search";
    }

    internal void MountContent(Control host)
    {
        if (_content == null) _Ready();
        if (_content.GetParent() != host) _content.Reparent(host, false);
        _content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _content.SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_preview != null) return;
        float scale = EditorInterface.Singleton.GetEditorScale();
        _content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _content.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(_content);
        var identity = new HBoxContainer(); _content.AddChild(identity);
        _title = new Label { Text = "Select an asset", ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        identity.AddChild(_title);
        identity.AddChild(AssetUi.Action("ActionCopy", "Copy selected asset ID", () => { if (Current != null) DisplayServer.ClipboardSet(Current.Id); }));
        _origin = AssetUi.Label("Browse the Assets library to inspect a selection."); _content.AddChild(_origin);
        var previewScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 160 * scale), SizeFlagsVertical = SizeFlags.Fill };
        _previewArea = previewScroll;
        _content.AddChild(previewScroll);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        previewScroll.AddChild(center);
        _preview = new TextureRect { StretchMode = TextureRect.StretchModeEnum.Keep, ExpandMode = TextureRect.ExpandModeEnum.KeepSize, TextureFilter = TextureFilterEnum.Nearest };
        center.AddChild(_preview);
        var row = new HBoxContainer(); _content.AddChild(row);
        _previewControls = row;
        _dimensions = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(_dimensions);
        _zoom = new OptionButton { AccessibilityName = "Pixel preview zoom", TooltipText = "Integer zoom · nearest-neighbor sampling" };
        foreach (int z in new[] { 1, 2, 3, 4, 5, 6, 7, 8 }) _zoom.AddItem($"{z}×");
        _zoom.Selected = 1; _zoom.ItemSelected += _ => ShowFrame((int)_frame.Value); row.AddChild(_zoom);
        _player = new HBoxContainer { Visible = false }; _content.AddChild(_player);
        _play = AssetUi.Action("Pause", "Pause animation playback", () => { _playing = !_playing; UpdatePlayButton(); }); _player.AddChild(_play);
        _frame = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, Step = 1, AccessibilityName = "Animation frame" };
        _frame.ValueChanged += v => ShowFrame((int)v); _player.AddChild(_frame);
        _actions = new HBoxContainer(); _content.AddChild(_actions);
        var detailScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 60 * scale) };
        _content.AddChild(detailScroll);
        _pages = new TabContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        detailScroll.AddChild(_pages);
        _properties = new VBoxContainer { Name = "Properties", SizeFlagsHorizontal = SizeFlags.ExpandFill }; _pages.AddChild(_properties);
        Metadata = new ItemMetadataEditor { Visible = false }; _properties.AddChild(Metadata);
        Strings = new ClilocEditor { Visible = false }; _properties.AddChild(Strings);
        Sound = new SoundEditor { Visible = false }; _properties.AddChild(Sound);
        Hues = new HueEditor { Visible = false }; _properties.AddChild(Hues);
        Textures = new TextureEditor { Visible = false }; _properties.AddChild(Textures);
        Textures.Changed += () => { if (Current?.Texture != null) _history.Text = Textures.Summary; };
        Hues.Changed += () => { if (Current?.Hue != null) _history.Text = Hues.Summary; };
        Sound.Changed += () => { if (Current?.Sound != null) _history.Text = Sound.Summary; };
        Strings.Changed += () => { if (Current?.Strings != null) _history.Text = Strings.Summary; };
        Metadata.Changed += () =>
        {
            if (Current?.MetadataData != null && Metadata.CurrentDocument is { } document)
                _title.Text = $"{document.Current.Name} · {Current.Id}";
            _history.Text = Metadata.Summary;
        };
        _sourceFields = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill }; _properties.AddChild(_sourceFields);
        _fields = new RichTextLabel { BbcodeEnabled = true, FitContent = true, SelectionEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _properties.AddChild(_fields);
        _imagePage = new VBoxContainer { Name = "Image" }; _pages.AddChild(_imagePage);
        _history = AssetUi.Label("Document undo/redo is available while editing item metadata.");
        var history = new VBoxContainer { Name = "History" }; history.AddChild(_history); _pages.AddChild(history);
        Metadata._Ready(); Metadata.Footer.Reparent(_content, false); Metadata.Footer.Visible = false;
        Strings._Ready(); Strings.Footer.Reparent(_content, false); Strings.Footer.Visible = false;
        Sound._Ready(); Sound.Footer.Reparent(_content, false); Sound.Footer.Visible = false;
        Hues._Ready(); Hues.Footer.Reparent(_content, false); Hues.Footer.Visible = false;
        Textures._Ready(); Textures.Footer.Reparent(_content, false); Textures.Footer.Visible = false;
    }

    public void ShowInspection(Inspection inspection)
    {
        if (_preview == null) _Ready();
        Strings.CommitPending();
        Sound.CommitPending();
        Hues.CommitPending();
        Current = inspection;
        _title.Text = inspection.Title != null ? $"{inspection.Title} · {inspection.Id}" : $"{inspection.Source} · {inspection.Id}";
        _title.TooltipText = _title.Text;
        _origin.Text = inspection.Origin ?? "Source details below";
        _fields.Text = inspection.Text;
        _fields.Visible = inspection.Fields.Count == 0 && inspection.Strings == null && inspection.Hue == null;
        _sourceFields.Visible = inspection.Strings == null;
        foreach (Node child in _sourceFields.GetChildren()) { _sourceFields.RemoveChild(child); child.QueueFree(); }
        foreach (var (name, value) in inspection.Fields)
        {
            _sourceFields.AddChild(new Label { Text = name + "  ", SizeFlagsVertical = SizeFlags.ShrinkBegin });
            _sourceFields.AddChild(new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        }
        Metadata.Visible = inspection.MetadataData != null;
        Metadata.Footer.Visible = Metadata.Visible;
        if (Metadata.Visible) Metadata.Bind(inspection.MetadataData, inspection.ArtId);
        else Metadata.CommitPending();
        Strings.Visible = inspection.Strings != null; Strings.Footer.Visible = Strings.Visible;
        if (Strings.Visible) Strings.Bind(inspection.Strings, inspection.StringId, inspection.StringsChanged);
        Sound.Visible = inspection.Sound != null; Sound.Footer.Visible = Sound.Visible;
        if (Sound.Visible) Sound.Bind(inspection.Sound, inspection.SoundChanged);
        Hues.Visible = inspection.Hue != null; Hues.Footer.Visible = Hues.Visible;
        if (Hues.Visible) Hues.Bind(inspection.Hue, inspection.HueChanged);
        Textures.Visible = inspection.Texture != null; Textures.Footer.Visible = Textures.Visible;
        if (Textures.Visible) Textures.Bind(inspection.Texture, inspection.TextureChanged);
        _history.Text = Textures.Visible ? Textures.Summary : Hues.Visible ? Hues.Summary : Sound.Visible ? Sound.Summary : Strings.Visible ? Strings.Summary : Metadata.Visible ? Metadata.Summary : "No project document for this asset.";
        _previewArea.Visible = _previewControls.Visible = inspection.Image != null;
        _pages.SetTabHidden(1, inspection.Image == null);
        if (inspection.Image == null && _pages.CurrentTab == 1) _pages.CurrentTab = 0;
        BuildActions(inspection);
        _actions.Visible = _actions.GetChildCount() > 0;
        _dimensions.Text = inspection.Image == null ? "" : $"{inspection.Image.GetWidth()} × {inspection.Image.GetHeight()} px";
        int frames = inspection.Frames.Length;
        _player.Visible = frames > 1; _frame.MaxValue = Math.Max(0, frames - 1); _frame.SetValueNoSignal(0);
        _playing = frames > 1; _clock = 0; UpdatePlayButton(); ShowFrame(0);
    }

    private void BuildActions(Inspection inspection)
    {
        foreach (Node child in _actions.GetChildren()) { _actions.RemoveChild(child); child.QueueFree(); }
        foreach (Node child in _imagePage.GetChildren()) { _imagePage.RemoveChild(child); child.QueueFree(); }
        var more = new MenuButton { Icon = AssetUi.Icon("GuiTabMenuHl"), TooltipText = "More asset actions", AccessibilityName = "More asset actions" };
        int menuIndex = 0;
        foreach (InspectionAction action in inspection.Actions)
        {
            if (action.Children.Count > 0)
            {
                var settings = EditorInterface.Singleton.GetEditorSettings();
                string preferred = settings.GetProjectMetadata("guo_assets", "image_editor", "pixelorama-tab").AsString();
                InspectionAction choice = action.Children.Find(c => c.Id == preferred) ?? action.Children[0];
                var edit = AssetUi.Action(action.Icon, choice.Label, () => choice.Run()); _actions.AddChild(edit);
                var destinations = new MenuButton { Icon = AssetUi.Icon("GuiOptionArrow"), TooltipText = "Choose image editor", AccessibilityName = "Choose image editor" };
                foreach (InspectionAction child in action.Children) destinations.GetPopup().AddItem(child.Label);
                destinations.GetPopup().IdPressed += id =>
                {
                    choice = action.Children[(int)id]; settings.SetProjectMetadata("guo_assets", "image_editor", choice.Id);
                    edit.TooltipText = edit.AccessibilityName = choice.Label;
                    try { choice.Run(); } catch (Exception e) { AssetActions.ReportEdit(e.Message); }
                };
                _actions.AddChild(destinations);
                continue;
            }
            if (action.Primary) _actions.AddChild(AssetUi.Action(action.Icon, action.Label, action.Run));
            else
            {
                int index = menuIndex++;
                more.GetPopup().AddIconItem(AssetUi.Icon(action.Icon), action.Label, index);
                more.GetPopup().IdPressed += id => { if (id == index) { try { action.Run(); } catch (Exception e) { AssetActions.ReportEdit(e.Message); } } };
            }
            if (action.Id is "replace-image" or "export-png" or "review-stage")
            {
                var button = AssetUi.Action(action.Icon, action.Label, action.Run); button.Text = action.Label;
                _imagePage.AddChild(button);
            }
        }
        if (menuIndex > 0) _actions.AddChild(more); else more.QueueFree();
        _imagePage.AddChild(AssetUi.Label("Image and metadata working copies stay in the project. Native exports require a separate reviewed stage."));
    }

    private void UpdatePlayButton()
    {
        _play.Icon = AssetUi.Icon(_playing ? "Pause" : "Play");
        _play.TooltipText = _play.AccessibilityName = _playing ? "Pause animation playback" : "Play animation";
    }

    private void ShowFrame(int index)
    {
        Image img = Current != null && index < Current.Frames.Length ? Current.Frames[index] : null;
        if (img == null) { _preview.Texture = null; return; }
        int z = _zoom.Selected + 1;
        if (z > 1) { img = (Image)img.Duplicate(); img.Resize(img.GetWidth() * z, img.GetHeight() * z, Image.Interpolation.Nearest); }
        _preview.Texture = ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        if (!_playing || Current == null || Current.Frames.Length < 2) return;
        _clock += delta;
        double step = 1.0 / Math.Max(1, Current.Fps);
        if (_clock >= step) { _clock -= step; _frame.Value = ((int)_frame.Value + 1) % Current.Frames.Length; }
    }
}
#endif
