#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;

/// <summary>The World workspace: icon tools, reusable brushes and precision editing.</summary>
public partial class WorldView
{
    public event Action FocusRequested;
    private WorldTool _activeTool;
    private HBoxContainer _legacyTools, _legacyModes, _commandBar;
    private Control _library, _settings, _quickFavorites;
    private VSplitContainer _leftWorkspace;
    private TabContainer _detailTabs;
    private HSplitContainer _librarySplit;
    private ScrollContainer _commonTools;
    private double _toolHeightRatio = 0.65;
    internal VBoxContainer InspectorHost { get; private set; }
    internal ArtPanel BrushLibrary => _brushArt;
    private WaterPanel _water;
    internal WaterPanel WaterWorkspace => _water;
    internal SpinBox BrushSizeInput => _numbers["Size (tiles)"];
    internal void ShowInspectorTab(bool inspector) => _detailTabs.CurrentTab = inspector ? 0 : 1;
    // Methods deliberately: Godot serializes settable properties during assembly reload.
    internal Vector2I GetWorkspaceSplit() => new(((HSplitContainer)_leftWorkspace.GetParent()).SplitOffsets[0], _leftWorkspace.SplitOffsets[0]);
    internal void SetWorkspaceSplit(Vector2I value)
    { _toolHeightRatio = 0.5 + value.Y / Math.Max(1.0, _leftWorkspace.Size.Y); ((HSplitContainer)_leftWorkspace.GetParent()).SplitOffsets = new[] { value.X }; _leftWorkspace.SplitOffsets = new[] { value.Y }; }
    internal float ToolsHeight => _settings.Size.Y;
    private ArtPanel _brushArt;
    private readonly Dictionary<WorldTool, Button> _toolButtons = new();
    private readonly WorldBrush _recipe = new();
    private readonly HashSet<(int X, int Y)> _stroke = new();
    private const int StrokeCellLimit = 8192;
    private bool _painting, _spacePan, _strokeCapped;
    private bool _focusMode, _previousDistractionFree, _previousLibraryVisible;
    private bool _autoLibrary = true;
    private WorldBrush _strokeRecipe;
    private uint _strokeArt;
    private int _strokeVersion;
    private (int X, int Y)? _lastStroke;
    private readonly Dictionary<string, SpinBox> _numbers = new();
    private readonly Dictionary<string, CheckBox> _checks = new();
    private OptionButton _operation, _targetPick;
    private LineEdit _weights, _allowed, _edges, _presetName;
    private ItemList _presets, _stack;
    private CheckBox _lockTerrain;
    private Label _previewLabel, _historyLabel;
    private TextureRect _brushPreview;
    private readonly Dictionary<uint, Texture2D> _ghostTextures = new();
    private uint _previewArt = uint.MaxValue;
    private readonly List<uint> _favorites = new();
    private HFlowContainer _favoriteButtons;
    private VBoxContainer _variantRows;
    private readonly record struct StackEntry(int X, int Y, sbyte Z, ushort Id, bool Land, ushort Hue = 0, int Occurrence = 0);
    private readonly List<StackEntry> _stackEntries = new();
    private string _stackBlock;
    private int _stackVersion;
    private (int X, int Y)? _stackCell;
    private int _stackIndex = -1;
    private double _workspaceClock;

    private static Button ActionButton(Node parent, string text, Action action, string tip = "")
    {
        var button = new Button { Text = text, TooltipText = tip };
        button.Pressed += action; parent.AddChild(button); return button;
    }

    private static VBoxContainer Section(Node parent, string title, bool open = false)
    {
        var heading = new Button { Text = title, ToggleMode = true, ButtonPressed = open, Alignment = HorizontalAlignment.Left };
        parent.AddChild(heading);
        var content = new VBoxContainer { Visible = open };
        parent.AddChild(content); heading.Toggled += on => content.Visible = on;
        return content;
    }

    private SpinBox Number(Node parent, string label, double value, double min, double max, Action<int> change)
    {
        var row = new HBoxContainer(); parent.AddChild(row);
        var caption = new Label { Text = label switch { "Size (tiles)" => "Size", "Density (%)" => "Density %", "Spacing (tiles)" => "Spacing", "Z / ground offset" => "Z / offset", "Visible Z min" => "Z min", "Visible Z max" => "Z max", _ => label }, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(caption);
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, Step = 1, CustomMinimumSize = new Vector2(74, 0) };
        row.AddChild(spin); spin.ValueChanged += v => change((int)v); _numbers[label] = spin;
        NumericScrub.Attach(caption, spin); return spin;
    }

    private CheckBox Check(Node parent, string label, bool value, Action<bool> change)
    {
        var check = new CheckBox { Text = label, ButtonPressed = value };
        check.Toggled += on => change(on); parent.AddChild(check); _checks[label] = check; return check;
    }

    private void BuildWorkspace(HBoxContainer bar, HBoxContainer tools)
    {
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(body); MoveChild(body, 1);
        var railScroll = new ScrollContainer { CustomMinimumSize = new Vector2(48, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(railScroll);
        var rail = new VBoxContainer(); railScroll.AddChild(rail);
        var group = new ButtonGroup();
        var icons = new (WorldTool Tool, string Icon, string Shortcut)[]
        {
            (WorldTool.Select, "ToolSelect", "V"), (WorldTool.Brush, "Edit", "B"), (WorldTool.Stamp, "Duplicate", "S"),
            (WorldTool.Erase, "Remove", "E"), (WorldTool.Raise, "MoveUp", "R"), (WorldTool.Lower, "MoveDown", "F"),
            (WorldTool.Hue, "ColorPick", "H"),
            (WorldTool.Area, "ToolSelect", "G"), (WorldTool.PickRegion, "RegionEdit", "N"),
            (WorldTool.AudioZones, "AudioStreamPlayer", ""),
            (WorldTool.PlaceItem, "Node3D", ""), (WorldTool.PlaceSpawner, "MemberSignal", ""),
            (WorldTool.MoveObject, "ToolMove", ""), (WorldTool.DeleteObject, "Trash", ""),
            (WorldTool.Measure, "Ruler", "M"), (WorldTool.Route, "CurvePath", ""), (WorldTool.Pin, "Pin", ""),
        };
        foreach (var entry in icons)
        {
            if (entry.Tool is WorldTool.Area or WorldTool.PlaceItem or WorldTool.Measure) rail.AddChild(new HSeparator());
            Texture2D icon = EditorInterface.Singleton.GetBaseControl().GetThemeIcon(entry.Icon, "EditorIcons");
            var button = new Button { Icon = icon, Text = icon == null ? entry.Tool.ToString()[..1] : "", ToggleMode = true,
                ButtonGroup = group, CustomMinimumSize = new Vector2(38, 34), AccessibilityName = ToolName(entry.Tool), TooltipText = ToolName(entry.Tool) + (entry.Shortcut.Length > 0 ? " (" + entry.Shortcut + ")" : "") };
            button.Pressed += () => Tool = entry.Tool; rail.AddChild(button); _toolButtons[entry.Tool] = button;
        }

        var workspace = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffsets = new[] { 390 } };
        body.AddChild(workspace);
        _leftWorkspace = new VSplitContainer { CustomMinimumSize = new Vector2(350, 0) };
        workspace.AddChild(_leftWorkspace);
        _librarySplit = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { 280 } };
        workspace.AddChild(_librarySplit);
        var library = new VBoxContainer { CustomMinimumSize = new Vector2(240, 180) };
        _librarySplit.AddChild(library); _library = library;
        library.AddChild(new Label { Text = "Brush library" });
        var recipes = new HFlowContainer(); library.AddChild(recipes);
        ActionButton(recipes, "Single", () => QuickRecipe(false, "Paint", 1, 100, 1), "One item per click: a 1-tile, 100% brush with the selected art");
        ActionButton(recipes, "Scatter", () => QuickRecipe(false, "Paint", 7, 35, 2), "Many items over a 7-tile area at 35% density");
        ActionButton(recipes, "Terrain", () => QuickRecipe(true, "Paint", 5, 100, 1));
        ActionButton(recipes, "Sculpt", () => QuickRecipe(true, "Raise", 5, 100, 1));
        _brushArt = new ArtPanel { MinimumGridHeight = 80, ShowNames = true, Autocomplete = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        _brushArt.SetCellSize(80);
        _brushArt.Attach(_data); library.AddChild(_brushArt);
        _brushArt.Inspect += ins =>
        {
            Inspect?.Invoke(ins); _previewArt = uint.MaxValue;
            if (ins.ArtKind is AssetKind.Land or AssetKind.Static)
            {
                bool land = ins.ArtKind == AssetKind.Land;
                if (_recipe.Land != land) { _recipe.Variants = ""; if (_weights != null) _weights.Text = ""; RebuildVariants(); }
                _recipe.Land = land; _targetPick?.Select(land ? 1 : 0);
            }
        };
        if (_data != null) { _data.Loaded += OnBrushDataLoaded; if (_data.IsLoaded) OnBrushDataLoaded(); }
        var favorite = new HBoxContainer(); library.AddChild(favorite);
        ActionButton(favorite, "+ Favorite", AddFavorite);
        ActionButton(favorite, "+ Variation", () =>
        {
            uint art = _data?.CurrentArt ?? 0;
            uint id = art < EditorData.LandCount ? art : art - EditorData.LandCount;
            _weights.Text = (_weights.Text.Length == 0 ? "" : _weights.Text + ", ") + $"0x{id:X4}:1";
            _recipe.Variants = _weights.Text;
            RebuildVariants();
        });
        _favoriteButtons = new HFlowContainer(); library.AddChild(_favoriteButtons);
        var presetSection = Section(library, "Saved presets");
        _presets = new ItemList { CustomMinimumSize = new Vector2(0, 70), TooltipText = "Double-click a saved brush recipe to load it" };
        presetSection.AddChild(_presets); _presets.ItemActivated += i => ReadPreset(PresetName((int)i));
        _presetName = new LineEdit { PlaceholderText = "Name this brush preset" }; presetSection.AddChild(_presetName);
        var presetActions = new HBoxContainer(); presetSection.AddChild(presetActions);
        ActionButton(presetActions, "Save preset", SaveBrushPreset);
        ActionButton(presetActions, "Load", () => { var s = _presets.GetSelectedItems(); if (s.Length > 0) ReadPreset(PresetName(s[0])); });

        _stage.Reparent(_librarySplit);
        var settingsTabs = new TabContainer { CustomMinimumSize = new Vector2(0, 160), SizeFlagsVertical = SizeFlags.ExpandFill };
        _leftWorkspace.AddChild(settingsTabs); _settings = settingsTabs;
        var scroll = new ScrollContainer { Name = "Tools", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        settingsTabs.AddChild(scroll); _commonTools = scroll;
        var options = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(options);
        options.AddThemeConstantOverride("separation", 3);
        var selected = new HBoxContainer(); options.AddChild(selected);
        _brushPreview = new TextureRect { CustomMinimumSize = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
        selected.AddChild(_brushPreview);
        _brush.Reparent(selected); _brush.CustomMinimumSize = Vector2.Zero; _brush.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var advancedScroll = new ScrollContainer { Name = "Advanced", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        settingsTabs.AddChild(advancedScroll); _advancedTools = advancedScroll;
        _water = new WaterPanel { Name = "Water" };
        _water.Attach(() => _host.World);
        settingsTabs.AddChild(_water);
        BuildBoundaryControls(settingsTabs);
        var advancedOptions = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; advancedScroll.AddChild(advancedOptions);
        var editors = new HBoxContainer(); advancedOptions.AddChild(editors);
        ActionButton(editors, "Pixelorama", () => EditBrushArt(false), "Project > GUO: save back to GUO returns the edit");
        ActionButton(editors, "Pinta", () => EditBrushArt(true), "Save the exported PNG to return it");
        _detailTabs = new TabContainer { CustomMinimumSize = new Vector2(0, 180), SizeFlagsVertical = SizeFlags.ExpandFill };
        _leftWorkspace.AddChild(_detailTabs);
        InspectorHost = new VBoxContainer { Name = "UO Inspector" }; _detailTabs.AddChild(InspectorHost);
        BuildNearbyTiles();
        var brushSettings = options;
        var operationRow = new HBoxContainer(); brushSettings.AddChild(operationRow);
        _operation = new OptionButton();
        foreach (string op in new[] { "Paint", "Erase statics", "Hue statics", "Raise", "Lower", "Flatten", "Smooth" }) _operation.AddItem(op);
        _operation.ItemSelected += i => _recipe.Operation = _operation.GetItemText((int)i); operationRow.AddChild(_operation); _operation.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _targetPick = new OptionButton(); _targetPick.AddItem("Statics"); _targetPick.AddItem("Terrain");
        _targetPick.ItemSelected += i => { SetBrushTarget(i == 1); _brushArt.SelectKind(i == 1); _brushArt.Search(""); };
        operationRow.AddChild(_targetPick); _targetPick.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var values = new GridContainer { Columns = 2 }; brushSettings.AddChild(values);
        Number(values, "Size (tiles)", 7, 1, 31, v => _recipe.Size = v);
        Number(values, "Density (%)", 35, 0, 100, v => _recipe.Density = v);
        Number(values, "Spacing (tiles)", 2, 1, 16, v => _recipe.Spacing = v);
        Number(values, "Strength", 1, 1, 32, v => _recipe.Strength = v);
        var hueRow = new HBoxContainer(); values.AddChild(hueRow);
        var hueLabel = new Label { Text = "Hue", SizeFlagsHorizontal = SizeFlags.ExpandFill }; hueRow.AddChild(hueLabel);
        _hue.Reparent(hueRow); _hue.CustomMinimumSize = new Vector2(74, 0); _hue.TooltipText = "Hue: a name or an id, 0 for none; applies to Stamp and brush painting/recoloring";
        _hue.Committed += v => _recipe.Hue = (ushort)v;

        var terrainRecipes = Section(advancedOptions, "Landscape recipes");
        var landscape = new HFlowContainer();
        terrainRecipes.AddChild(landscape);
        ActionButton(landscape, "Coast", () => LandscapeRecipe(true, 3, 100, 1, 8),
            "Narrow terrain paint that avoids water. Select a shore tile and add compatible edge rules.");
        ActionButton(landscape, "Footpath", () => LandscapeRecipe(true, 2, 100, 1, 5),
            "Two-tile terrain paint for paths. Choose the ground material and limit allowed land IDs as needed.");
        ActionButton(landscape, "Woodland edge", () => LandscapeRecipe(false, 9, 28, 1, 10),
            "Seeded tree scatter that protects water and existing objects. Pick tree art or add weighted variants.");

        var variation = Section(advancedOptions, "Variation & rules", true);
        Number(variation, "Seed", 1, 0, 999999, v => _recipe.Seed = v);
        _variantRows = new VBoxContainer(); variation.AddChild(_variantRows);
        variation.AddChild(new Label { Text = "Advanced weights: ID:weight" });
        _weights = new LineEdit { PlaceholderText = "Blank = selected art" }; variation.AddChild(_weights);
        _weights.TextChanged += t => _recipe.Variants = t;
        _weights.TextSubmitted += _ => RebuildVariants();
        Check(variation, "Keep existing statics", true, v => _recipe.KeepStatics = v);
        Check(variation, "Avoid water", true, v => _recipe.AvoidWater = v);
        Number(variation, "Maximum slope", 127, 0, 127, v => _recipe.MaxSlope = v);
        _allowed = new LineEdit { PlaceholderText = "Allowed land IDs (blank = any)", TooltipText = "Comma-separated decimal or 0x IDs. Restrict painting to grass to avoid roads." };
        variation.AddChild(_allowed); _allowed.TextChanged += t => _recipe.AllowedLand = t;
        _edges = new LineEdit { PlaceholderText = "Terrain edge rules: mask=ID", TooltipText = "Optional transitions: N=1 E=2 S=4 W=8. Example 3=0x123. Connects to existing terrain using the chosen tile, variants and edge IDs as one family." };
        variation.AddChild(_edges); _edges.TextChanged += t => _recipe.Edges = t;

        Number(values, "Z / ground offset", 0, -128, 127, v => _recipe.Height = v);
        Number(values, "Visible Z min", -128, -128, 127, v => _host.MinVisibleZ = v);
        Number(values, "Visible Z max", 127, -128, 127, v => _host.MaxVisibleZ = v);
        var toggles = new GridContainer { Columns = 2 }; options.AddChild(toggles);
        Check(toggles, "Square footprint", false, v => _recipe.Square = v).Text = "Square";
        Check(toggles, "Fixed placement plane", false, v => _recipe.FixedHeight = v).Text = "Fixed Z";
        Check(toggles, "Ghost roofs", false, v => _host.GhostRoofs = v);
        _lockTerrain = Check(toggles, "Lock terrain", false, _ => { });
        var visibility = new HBoxContainer(); options.AddChild(visibility);
        _layers.Reparent(visibility); _guideMenu.Reparent(visibility);
        ActionButton(visibility, "Pick", PickBrushFromWorld, "Alt-click the map to sample art and hue");
        ActionButton(visibility, "Set Z / hue", TransformStackSelection, "Apply to the selected Nearby stack row");
        var advanced = Section(advancedOptions, "World & overlays");
        // Preserve all existing commands, but stop reserving two viewport-wide rows for them.
        foreach (Node child in tools.GetChildren().ToArray())
        {
            if (child == _tool) { _tool.Hide(); continue; }
            if (child is VSeparator) continue;
            child.Reparent(advanced);
        }
        tools.Hide();
        foreach (Node child in _legacyModes.GetChildren().ToArray()) child.Reparent(advanced);
        _legacyModes.Hide();
        // The View and Map layers menus stay on the top bar, beside Reload
        // project: overlays are one click away instead of buried in Advanced.
        // (Scene pack and the cursor readout stay in World & overlays.)
        _viewMenu.Reparent(bar);
        _layerMenu.Reparent(bar);
        bar.MoveChild(_viewMenu, _layersAgain.GetIndex() + 1);
        bar.MoveChild(_layerMenu, _layersAgain.GetIndex() + 2);
        _historyLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var history = Section(advancedOptions, "History"); history.AddChild(_historyLabel);

        // Status belongs below the canvas. Top remains one compact command row.
        _status.Reparent(this);
        _status.CustomMinimumSize = Vector2.Zero;
        _commandBar = bar;
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }; bar.AddChild(spacer);
        ActionButton(bar, "Undo", () => _editor.Undo(), "Ctrl+Z");
        ActionButton(bar, "Redo", () => _editor.Redo(), "Ctrl+Y");
        ActionButton(bar, "Brushes", () =>
        {
            _autoLibrary = false;
            _leftWorkspace.Show();
            _library.Visible = !_library.Visible;
        }, "Show or hide the brush library");
        ActionButton(bar, "Precision", () =>
        {
            _autoLibrary = false;
            _leftWorkspace.Show();
            _library.Hide();
            _settings.Show();
        });
        ActionButton(bar, "Focus", ToggleWorkspaceFocus, "Tab: hide panels and Godot docks");
        _previewLabel = new Label { ClipText = true, MouseFilter = MouseFilterEnum.Ignore };
        _stage.AddChild(_previewLabel);
        _previewLabel.SetAnchorsPreset(LayoutPreset.TopWide);
        _previewLabel.OffsetLeft = 12;
        _previewLabel.OffsetRight = -12;
        _previewLabel.OffsetTop = 12;
        _previewLabel.OffsetBottom = 40;
        Resized += UpdateCompactWorkspace;
        Callable.From(UpdateCompactWorkspace).CallDeferred();
        var quick = new PanelContainer { Visible = false, Position = new Vector2(24, 60) };
        _stage.AddChild(quick); _quickFavorites = quick;
        SyncToolButtons(); LoadBrushPresets();
        var preferences = EditorInterface.Singleton.GetEditorSettings();
        workspace.SplitOffsets = new[] { preferences.GetProjectMetadata("guo_world", "tools_width_v2", 390).AsInt32() };
        _librarySplit.SplitOffsets = new[] { preferences.GetProjectMetadata("guo_world", "brush_width_v2", 280).AsInt32() };
        _toolHeightRatio = Math.Clamp(preferences.GetProjectMetadata("guo_world", "tools_ratio_v3", 0.65).AsDouble(), 0.25, 0.8);
        AddScrollCues();
        options.MinimumSizeChanged += ApplyToolHeightRatio;
        _leftWorkspace.Resized += ApplyToolHeightRatio;
        Callable.From(ApplyToolHeightRatio).CallDeferred();
        workspace.Dragged += offset => preferences.SetProjectMetadata("guo_world", "tools_width_v2", offset);
        _librarySplit.Dragged += offset => preferences.SetProjectMetadata("guo_world", "brush_width_v2", offset);
        _leftWorkspace.Dragged += offset =>
        {
            if (_leftWorkspace.Size.Y > 0) _toolHeightRatio = Math.Clamp(0.5 + offset / _leftWorkspace.Size.Y, 0.25, 0.8);
            preferences.SetProjectMetadata("guo_world", "tools_ratio_v3", _toolHeightRatio);
        };
    }

    // Presets reload too: their rows name what they paint, which needs the art names.
    private void OnBrushDataLoaded() { _brushArt?.OnDataLoaded(); LoadBrushPresets(); RebuildVariants(); }
    internal bool TerrainLocked { get => _lockTerrain?.ButtonPressed == true; set { if (_lockTerrain != null) _lockTerrain.ButtonPressed = value; } }
    internal string BrushStatus => _status?.Text;
    // The active tool and, for the brush, which recipe it is: "Brush · Single 1×1 100%", "Brush · Scatter 7×7 35%".
    internal string ToolLabel
    {
        get
        {
            if (Tool != WorldTool.Brush) return ToolName(Tool);
            string kind = _recipe.Operation == "Paint"
                ? (_recipe.Land ? "Terrain" : _recipe.Size == 1 && _recipe.Density == 100 ? "Single" : "Scatter")
                : _recipe.Operation;
            int n = Math.Clamp(_recipe.Size, 1, 31);
            return $"Brush · {kind} {n}×{n} {_recipe.Density}%";
        }
    }
    internal void QuickRecipeForSmoke(string name)
    {
        switch (name)
        {
            case "Single": QuickRecipe(false, "Paint", 1, 100, 1); break;
            case "Scatter": QuickRecipe(false, "Paint", 7, 35, 2); break;
            default: throw new ArgumentException(name);
        }
    }
    internal void PlaceForSmoke(IEnumerable<(int X, int Y)> cells)
    {
        _stroke.Clear(); _stroke.UnionWith(cells); CommitStroke();
    }
    internal void SetKeepStaticsForSmoke(bool keep) { _recipe.KeepStatics = keep; if (_checks.TryGetValue("Keep existing statics", out var c)) c.ButtonPressed = keep; }
    internal void ApplyBrushForSmoke(WorldBrush brush, IEnumerable<(int X, int Y)> cells)
    {
        _recipe.Operation = brush.Operation; _recipe.Land = brush.Land; _recipe.Height = brush.Height;
        _recipe.Density = brush.Density; _recipe.Spacing = brush.Spacing;
        _stroke.Clear(); _stroke.UnionWith(cells); CommitStroke();
    }
    internal void ShowWorkspacePreview()
    {
        QuickRecipe(false, "Paint", 7, 35, 2);
        if (_data?.HasArt(EditorData.LandCount + 0x0CCA) == true) _data.CurrentArt = EditorData.LandCount + 0x0CCA;
        _numbers["Z / ground offset"].Value = 0;
        _recipe.FixedHeight = false; _checks["Fixed placement plane"].ButtonPressed = false;
        BrushHue = 0;
        _library.Show(); _settings.Show();
        _leftWorkspace.Show();
        SetToggle("Blocks", false);
        _brushArt.Search("tree");
        EditorInterface.Singleton.SetDistractionFreeMode(true);
        FocusRequested?.Invoke();
    }

    private void QuickRecipe(bool land, string operation, int size, int density, int spacing)
    {
        _recipe.Land = land; _targetPick.Select(land ? 1 : 0);
        _recipe.Operation = operation;
        for (int i = 0; i < _operation.ItemCount; i++) if (_operation.GetItemText(i) == operation) _operation.Select(i);
        _numbers["Size (tiles)"].Value = size; _numbers["Density (%)"].Value = density; _numbers["Spacing (tiles)"].Value = spacing;
        _weights.Text = ""; _recipe.Variants = ""; RebuildVariants();
        _brushArt.SelectKind(land); _brushArt.Search(""); Tool = WorldTool.Brush;
    }

    private void LandscapeRecipe(bool land, int size, int density, int spacing, int slope)
    {
        QuickRecipe(land, "Paint", size, density, spacing);
        _numbers["Seed"].Value = 1;
        _numbers["Maximum slope"].Value = slope;
        _checks["Avoid water"].ButtonPressed = true;
        _checks["Keep existing statics"].ButtonPressed = true;
        _status.Text = land
            ? "Choose a land tile. Use Height and Reachability to check slopes and connections."
            : "Choose a tree or shrub. Add weighted variations for an irregular woodland edge.";
    }

    private void RebuildVariants()
    {
        if (_variantRows == null) return;
        foreach (Node n in _variantRows.GetChildren()) { _variantRows.RemoveChild(n); n.QueueFree(); }
        var variants = WorldBrush.ParseVariants(_recipe.Variants);
        if (variants.Count == 0) { _variantRows.AddChild(new Label { Text = "Use + Variation to mix selected assets" }); return; }
        int total = variants.Sum(v => v.Weight);
        for (int i = 0; i < Math.Min(12, variants.Count); i++)
        {
            int index = i; var v = variants[i]; uint art = (_recipe.Land ? 0 : EditorData.LandCount) + v.Id;
            var row = new HBoxContainer(); _variantRows.AddChild(row);
            using Image img = _data?.ArtImage(art);
            row.AddChild(new TextureRect { Texture = img == null ? null : ImageTexture.CreateFromImage(img), CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest });
            row.AddChild(new Label { Text = NameThenId(art, v.Id), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var weight = new SpinBox { Value = v.Weight, MinValue = 1, MaxValue = 10000, TooltipText = $"Relative weight ({v.Weight * 100 / total}% at these settings)" };
            row.AddChild(weight);
            weight.ValueChanged += value =>
            {
                var current = WorldBrush.ParseVariants(_recipe.Variants);
                if (index >= current.Count) return;
                current[index] = (current[index].Id, (int)value);
                _recipe.Variants = _weights.Text = string.Join(", ", current.Select(t => $"0x{t.Id:X4}:{t.Weight}"));
            };
            ActionButton(row, "×", () =>
            {
                var current = WorldBrush.ParseVariants(_recipe.Variants); if (index < current.Count) current.RemoveAt(index);
                _recipe.Variants = _weights.Text = string.Join(", ", current.Select(t => $"0x{t.Id:X4}:{t.Weight}")); RebuildVariants();
            });
        }
    }
    private void SyncToolButtons()
    {
        foreach (var pair in _toolButtons) pair.Value.SetPressedNoSignal(pair.Key == Tool);
    }

    private void UpdateCompactWorkspace()
    {
        if (_autoLibrary && !_focusMode && Size.X > 0)
            _library.Visible = Size.X >= 1150 * EditorInterface.Singleton.GetEditorScale();
    }

    private void ToggleWorkspaceFocus()
    {
        if (!_focusMode)
        {
            _previousDistractionFree = EditorInterface.Singleton.IsDistractionFreeModeEnabled();
            _previousLibraryVisible = _library.Visible;
            _focusMode = true;
            _leftWorkspace.Hide();
            _library.Hide();
            EditorInterface.Singleton.SetDistractionFreeMode(true);
            FocusRequested?.Invoke();
        }
        else
        {
            _focusMode = false;
            _leftWorkspace.Show();
            _library.Visible = _previousLibraryVisible;
            EditorInterface.Singleton.SetDistractionFreeMode(_previousDistractionFree);
            UpdateCompactWorkspace();
        }
    }

    private void EditBrushArt(bool pinta)
    {
        if (_data?.IsLoaded != true) { _status.Text = "Client art is still loading"; return; }
        uint art = _data.CurrentArt;
        Image image = _data.ArtImage(art);
        if (image == null) { _status.Text = "Choose an asset in the brush library first"; return; }
        try
        {
            string why = AssetActions.EditIn(_data, art < EditorData.LandCount ? AssetKind.Land : AssetKind.Static,
                (int)(art < EditorData.LandCount ? art : art - EditorData.LandCount), image, pinta);
            _status.Text = why ?? (pinta ? "Pinta opened: save the PNG to return it to GUO" : "Pixelorama opened: GUO > Save back to GUO returns the edit");
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void AddFavorite()
    {
        if (_data == null) return;
        if (!_favorites.Contains(_data.CurrentArt)) _favorites.Add(_data.CurrentArt);
        if (_favorites.Count > 12) _favorites.RemoveAt(0);
        RebuildFavorites(); SaveFavorites();
    }

    private void RebuildFavorites()
    {
        if (_favoriteButtons == null) return;
        foreach (Node n in _favoriteButtons.GetChildren()) { _favoriteButtons.RemoveChild(n); n.QueueFree(); }
        foreach (uint id in _favorites)
        {
            var b = ActionButton(_favoriteButtons, "", () => UseFavorite(id), NameThenId(id, (int)(id < EditorData.LandCount ? id : id - EditorData.LandCount)));
            Image img = _data?.ArtImage(id);
            if (img != null) b.Icon = ImageTexture.CreateFromImage(img);
            b.ExpandIcon = true; b.CustomMinimumSize = new Vector2(38, 38); b.TextureFilter = TextureFilterEnum.Nearest;
        }
    }

    private void UseFavorite(uint id)
    {
        _data.CurrentArt = id;
        SetBrushTarget(id < EditorData.LandCount);
        Tool = WorldTool.Brush;
    }

    private void SetBrushTarget(bool land)
    {
        if (_recipe.Land != land)
        {
            _recipe.Variants = _weights.Text = "";
            RebuildVariants();
        }
        _recipe.Land = land;
        _targetPick.Select(land ? 1 : 0);
    }

    private void ShowQuickFavorites()
    {
        foreach (Node n in _quickFavorites.GetChildren()) { _quickFavorites.RemoveChild(n); n.QueueFree(); }
        var list = new VBoxContainer(); _quickFavorites.AddChild(list);
        list.AddChild(new Label { Text = "Quick favorites · Q / Esc to close" });
        foreach (uint id in _favorites)
            ActionButton(list, NameThenId(id, (int)(id < EditorData.LandCount ? id : id - EditorData.LandCount)), () => { _quickFavorites.Hide(); UseFavorite(id); });
        if (_favorites.Count == 0) list.AddChild(new Label { Text = "Use + Favorite in the brush library" });
        _quickFavorites.Visible = !_quickFavorites.Visible;
    }

    private void UpdateWorkspace(double delta)
    {
        _workspaceClock += delta;
        if (_data?.IsLoaded == true && _previewArt != _data.CurrentArt)
        {
            _previewArt = _data.CurrentArt;
            Image image = _data.ArtImage(_previewArt);
            _brushPreview.Texture = image == null ? null : ImageTexture.CreateFromImage(image);
            _brush.TooltipText = $"{(_previewArt < EditorData.LandCount ? "Land" : "Static")} · {_data.NameOf(_previewArt)}";
        }
        if (_workspaceClock < 0.10) return;
        _workspaceClock = 0;
        UpdateNearbyTiles();
        _historyLabel.Text = $"Undo {_editor.UndoCount} · Redo {_editor.RedoCount}\n{_editor.LastWhat}";
        if (_pinNearby?.ButtonPressed != true && _host.Picked is GameObject o && !_painting && _stackIndex < 0 && _stackCell != ((int)o.X, (int)o.Y)) RefreshStack(o.X, o.Y);
        _guides.BrushCells.Clear();
        _guides.BrushRepairs.Clear();
        _guides.Ghosts.Clear();
        _guides.RoofGhosts.Clear();
        if (_host.GhostRoofs && _data?.IsLoaded == true)
        {
            CellGeometry geometry = CellGeometry.From(_host);
            if (geometry != null)
            foreach (var cell in geometry.Cells())
            {
                var chunk = _host.World.Map.GetChunk2(cell.X >> 3, cell.Y >> 3, false);
                for (GameObject roof = chunk?.GetHeadObject(cell.X & 7, cell.Y & 7); roof != null; roof = roof.TNext)
                {
                    if (roof is not Static st || !st.ItemData.IsRoof || roof.Z < _host.MinVisibleZ || roof.Z > _host.MaxVisibleZ || _guides.RoofGhosts.Count >= 512) continue;
                    var texture = GhostTexture(EditorData.LandCount + roof.Graphic);
                    if (texture != null) _guides.RoofGhosts.Add((roof.X, roof.Y, roof.Z, texture));
                }
            }
        }
        string destination = Tool switch
        {
            WorldTool.Brush or WorldTool.Stamp or WorldTool.Erase or WorldTool.Hue or WorldTool.Raise or WorldTool.Lower => "Map blocks",
            WorldTool.PlaceItem or WorldTool.PlaceSpawner or WorldTool.MoveObject or WorldTool.DeleteObject => "Project objects",
            WorldTool.Pin => "Project bookmarks",
            WorldTool.PickRegion => "Shard boundaries",
            WorldTool.AudioZones => "Client audio boundaries",
            _ => "Inspect & navigate",
        };
        _previewLabel.Text = $"{ToolLabel} · {destination} · map{_host.Facet}";
        _guides.PlaneZ = null;
        if (Tool != WorldTool.Brush || _host.Picked is not GameObject h || _modeNode?.Data == null) return;

        WorldBrush recipe = _strokeRecipe ?? _recipe;
        uint art = _strokeRecipe != null ? _strokeArt : _data?.CurrentArt ?? 0;
        string refusal = BrushRefusal(recipe, art);
        if (refusal != null)
        {
            _previewLabel.Text += $" · {refusal}";
            return;
        }

        var hoverCell = BrushCell(h);
        var plan = recipe.Plan(_modeNode.Data, _painting ? _stroke : recipe.Footprint(hoverCell.X, hoverCell.Y), BrushId(art));
        int repairs = 0;
        foreach (var c in plan.Cells)
        {
            if (c.Repair) { _guides.BrushRepairs.Add((c.X, c.Y)); repairs++; }
            else _guides.BrushCells.Add((c.X, c.Y));
            if (c.Kind != WorldBrush.EditKind.Add || _guides.Ghosts.Count >= 128) continue;
            Texture2D texture = GhostTexture(EditorData.LandCount + c.Id);
            if (texture != null) _guides.Ghosts.Add((c.X, c.Y, c.Z, texture));
        }
        _guides.PlaneZ = recipe.FixedHeight ? recipe.Height : null;
        _previewLabel.Text += $" · {plan.Cells.Count} to change · {plan.Skipped} skipped";
        if (repairs > 0) _previewLabel.Text += $" · {repairs} edge repairs";
        if (_strokeCapped) _previewLabel.Text += " · stroke limit";
        if (_painting) _previewLabel.Text += " · release to apply · Esc cancels";
    }

    private bool WorkspaceInput(InputEvent e)
    {
        if (e is InputEventKey { Keycode: Key.Space } space) { _spacePan = space.Pressed; return true; }
        if (e is InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, AltPressed: false } key)
        {
            switch (key.Keycode)
            {
                case Key.Tab: ToggleWorkspaceFocus(); return true;
                case Key.Q: ShowQuickFavorites(); return true;
                case Key.Escape:
                    if (_pointerButton != MouseButton.None) CancelPointer();
                    else if (BoundaryTool) ClearBoundary();
                    else
                    {
                        ClearArea();
                        _moving = null;
                        _mapLayers.Route.Start = null;
                        if (_mapLayers.Measure.B == null) _mapLayers.Measure.Clear();
                    }
                    _quickFavorites.Hide(); _stackIndex = -1;
                    return true;
                case Key.V: Tool = WorldTool.Select; return true;
                case Key.B: Tool = WorldTool.Brush; return true;
                case Key.S: Tool = WorldTool.Stamp; return true;
                case Key.E: Tool = WorldTool.Erase; return true;
                case Key.R: Tool = WorldTool.Raise; return true;
                case Key.F: Tool = WorldTool.Lower; return true;
                case Key.H: Tool = WorldTool.Hue; return true;
                case Key.M: Tool = WorldTool.Measure; return true;
                case Key.G: Tool = WorldTool.Area; return true;
                case Key.N: Tool = WorldTool.PickRegion; return true;
                case Key.Bracketleft: _numbers["Size (tiles)"].Value--; return true;
                case Key.Bracketright: _numbers["Size (tiles)"].Value++; return true;
            }
        }
        if (e is InputEventMouseButton { Pressed: true, AltPressed: true } alt)
        {
            if (alt.ButtonIndex == MouseButton.Left && !_spacePan && _pointerButton == MouseButton.None)
            {
                RefreshPointerPick(alt.Position);
                if (_host.Picked is GameObject clicked && _stackCell != ((int)clicked.X, (int)clicked.Y))
                    _stackIndex = -1;
                PickBrushFromWorld();
                return true;
            }
            if (alt.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (_host.Picked is GameObject at && _stackIndex < 0) RefreshStack(at.X, at.Y);
                if (_stackEntries.Count > 0)
                {
                    _stackIndex = (_stackIndex + (alt.ButtonIndex == MouseButton.WheelUp ? 1 : _stackEntries.Count - 1) + _stackEntries.Count) % _stackEntries.Count;
                    _stack.Select(_stackIndex); InspectPicked(); AnnounceStackChoice();
                }
                return true;
            }
        }
        if (!_spacePan && _pointerButton == MouseButton.None && Tool == WorldTool.Select &&
            e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, ShiftPressed: true } select)
        {
            RefreshPointerPick(select.Position);
            _stackIndex = -1;
            if (_host.Picked is GameObject at) { SetArea(at.X, at.Y, at.X, at.Y); RefreshStack(at.X, at.Y); }
            InspectPicked();
            return true;
        }
        return false;
    }

    private void BeginStroke()
    {
        _painting = true;
        _strokeRecipe = _recipe.Snapshot();
        _strokeArt = _data?.CurrentArt ?? 0;
        _strokeVersion = _modeNode.Data.Version;
        _strokeCapped = false;
        _stroke.Clear();
        _lastStroke = null;
        ExtendStroke();
    }

    private void ExtendStroke()
    {
        if (_host.Picked is not GameObject at) return;
        var end = BrushCell(at);
        if (_lastStroke == end) return;
        var start = _lastStroke ?? end;
        int steps = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        for (int s = 0; s <= steps; s++)
        {
            int x = start.X + (end.X - start.X) * s / Math.Max(1, steps);
            int y = start.Y + (end.Y - start.Y) * s / Math.Max(1, steps);
            foreach (var c in (_strokeRecipe ?? _recipe).Footprint(x, y))
            {
                if (_stroke.Count < StrokeCellLimit) _stroke.Add(c);
                else if (!_stroke.Contains(c)) _strokeCapped = true;
            }
        }
        _lastStroke = end;
    }

    private (int X, int Y) BrushCell(GameObject at)
    {
        WorldBrush recipe = _strokeRecipe ?? _recipe;
        if (recipe.FixedHeight && CellGeometry.From(_host) is { } geometry)
        {
            Vector2 mouse = ForcedMouse is { } forced ? new Vector2(forced.X, forced.Y) : _container.GetLocalMousePosition();
            var c = geometry.CellAt(mouse, recipe.Height);
            return ((int)Math.Floor(c.X), (int)Math.Floor(c.Y));
        }
        return (at.X, at.Y);
    }

    private static ushort BrushId(uint art) => (ushort)(art < EditorData.LandCount ? art : art - EditorData.LandCount);

    private string BrushRefusal(WorldBrush recipe, uint art)
    {
        if (_host.Project == null) return "Open a world project to paint";
        if (recipe.ChangesTerrain && TerrainLocked) return "Terrain is locked";
        if (recipe.Operation == "Paint" && ((_data?.IsLoaded != true || !_data.HasArt(art)) ||
                (art < EditorData.LandCount) != recipe.Land))
            return "Choose art matching the target: terrain or statics";
        return recipe.Validate(_data);
    }

    private void CommitStroke()
    {
        try
        {
            WorldBrush recipe = _strokeRecipe ?? _recipe;
            uint art = _strokeRecipe != null ? _strokeArt : _data?.CurrentArt ?? 0;
            if (_strokeRecipe != null && _strokeVersion != _modeNode.Data.Version)
            {
                _status.Text = "Map changed during this stroke. Paint again to see the updated result.";
                return;
            }
            string invalid = BrushRefusal(recipe, art);
            if (invalid != null) { _status.Text = invalid; return; }
            var plan = recipe.Plan(_modeNode.Data, _stroke, BrushId(art));
            if (plan.Cells.Count == 0) _status.Text = plan.Reason ?? "Nothing changed";
            else if (plan.Apply(_editor, _host.Facet, recipe.Operation) && _strokeCapped)
                _status.Text += " · stroke limit reached";
            _stackIndex = -1;
            _stackCell = null;
        }
        catch (Exception ex) { _status.Text = $"Brush failed: {ex.Message}"; }
        finally { _strokeRecipe = null; _strokeCapped = false; _stroke.Clear(); _lastStroke = null; }
    }

    public override void _Input(InputEvent e)
    {
        // Cancel if the button is released over another control or outside this window.
        if (_pointerButton != MouseButton.None &&
            e is InputEventMouseButton { Pressed: false } release && release.ButtonIndex == _pointerButton &&
            !_container.GetGlobalRect().HasPoint(_container.GetGlobalMousePosition()))
            CancelPointer();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMWindowFocusOut) CancelPointer();
    }

    private Texture2D GhostTexture(uint art)
    {
        if (_ghostTextures.TryGetValue(art, out var texture)) return texture;
        if (_ghostTextures.Count >= 1024) _ghostTextures.Clear();
        using Image img = _data?.ArtImage(art);
        texture = img == null ? null : ImageTexture.CreateFromImage(img);
        _ghostTextures[art] = texture;
        return texture;
    }

    private void RefreshStack(int x, int y)
    {
        _stackCell = (x, y); _stackEntries.Clear(); _stack.Clear();
        _stackBlock = _host.Project?.BlockText(_host.Facet, x >> 3, y >> 3);
        _stackVersion = _modeNode.Data.Version;
        var occurrences = new Dictionary<(sbyte, ushort, ushort), int>();
        foreach (var s in _editor.StaticsAt(_host.Facet, x, y).OrderByDescending(s => s.Z))
        {
            if (s.Z < _host.MinVisibleZ || s.Z > _host.MaxVisibleZ) continue;
            var key = (s.Z, s.Id, s.Hue);
            int occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            _stackEntries.Add(new StackEntry(x, y, s.Z, s.Id, false, s.Hue, occurrence));
            _stack.AddItem($"{NameThenId(EditorData.LandCount + s.Id, s.Id)}\nZ {s.Z} · hue {s.Hue}", GhostTexture(EditorData.LandCount + s.Id));
        }
        BlockData b = _modeNode?.Data?.Block(x >> 3, y >> 3);
        if (b != null)
        {
            int i = (y & 7) * 8 + (x & 7);
            _stackEntries.Add(new StackEntry(x, y, b.LandZ[i], b.LandId[i], true));
            _stack.AddItem($"{NameThenId(b.LandId[i], b.LandId[i])} (ground)\nZ {b.LandZ[i]}", GhostTexture(b.LandId[i]));
        }
    }

    private GameObject EffectivePicked()
    {
        if (_stackIndex < 0 || _stackIndex >= _stackEntries.Count) return _host.Picked as GameObject;
        var s = _stackEntries[_stackIndex];
        if (_stackVersion != _modeNode.Data.Version) return null;
        var chunk = _host.World.Map.GetChunk2(s.X >> 3, s.Y >> 3, true);
        for (GameObject o = chunk?.GetHeadObject(s.X & 7, s.Y & 7); o != null; o = o.TNext)
            if ((s.Land ? o is Land : o is Static) && o.Graphic == s.Id && o.Z == s.Z && (s.Land || o.Hue == s.Hue)) return o;
        return null;
    }

    private void PickBrushFromWorld()
    {
        if (EffectivePicked() is not GameObject o || _data == null) return;
        SampleBrush(o);
    }

    private void SampleBrush(GameObject o) => SampleBrush(o.Graphic, o.Hue, o.Z, o is Land);

    private void SampleBrush(ushort art, ushort hue, sbyte z, bool land)
    {
        if (_data == null) return;
        _data.CurrentArt = land ? art : EditorData.LandCount + art;
        BrushHue = hue;
        SetBrushTarget(land);
        _status.Text = $"Picked {NameThenId(_data.CurrentArt, art)} · Z {z}";
    }

    private void TransformStackSelection()
    {
        if (_stackIndex < 0 || _stackIndex >= _stackEntries.Count)
        { _status.Text = "Set Z / hue: first click an item's row in Nearby tiles"; return; }
        var s = _stackEntries[_stackIndex];
        if (s.Land) { _status.Text = "Set Z / hue changes items, not the ground; use the Flatten brush to set ground height"; return; }
        string name = NameThenId(EditorData.LandCount + s.Id, s.Id);
        if (!EditStackRow(s, $"Set Z / hue: {name} now at Z {_recipe.Height}, hue {BrushHue}", item =>
        {
            item.Z = (sbyte)_recipe.Height;
            item.Hue = BrushHue;
            return item;
        })) return;
    }

    private bool EditStackRow(StackEntry entry, string what, Func<WorldStatic, WorldStatic?> change)
    {
        if (_stackVersion == _modeNode.Data.Version && _editor.EditStatic(_host.Facet, entry.X, entry.Y,
                entry.Z, entry.Id, entry.Hue, entry.Occurrence, _stackBlock, what, change))
        {
            _stackIndex = -1;
            RefreshStack(entry.X, entry.Y);
            return true;
        }

        _status.Text = "No static changed. Refresh Nearby tiles and choose the item again.";
        return false;
    }

    /// <summary>ED6: a Nearby row was chosen; the status line says which button applies the change.</summary>
    private void AnnounceStackChoice()
    {
        if (_stackIndex < 0 || _stackIndex >= _stackEntries.Count) return;
        var s = _stackEntries[_stackIndex];
        _status.Text = s.Land
            ? $"Chosen: the ground ({NameThenId(s.Id, s.Id)}) at Z {s.Z}. Set Z / hue changes items only; use the Flatten brush for ground"
            : $"Chosen: {NameThenId(EditorData.LandCount + s.Id, s.Id)} at Z {s.Z}. Type Z / ground offset and Hue, then press Set Z / hue";
    }

    /// <summary>ED6: an item or ground tile as its name first and its number second ("tree · 0x0CCA").</summary>
    private string NameThenId(uint art, int id)
    {
        string name = _data?.NameOf(art);
        return string.IsNullOrWhiteSpace(name) ? $"0x{id:X4}" : $"{name} · 0x{id:X4}";
    }

    private string PresetPath => Path.Combine(_host.Project?.Root ?? Path.Combine(EditorData.RepoRoot, "build", "world", "default"), "brushes.cfg");
    private ConfigFile ReadPresetFile() { var file = new ConfigFile(); file.Load(PresetPath); return file; }
    private void WritePresetFile(ConfigFile file) { Directory.CreateDirectory(Path.GetDirectoryName(PresetPath)); Error error = file.Save(PresetPath); if (error != Godot.Error.Ok) throw new IOException(error.ToString()); }
    private void SaveFavorites()
    {
        using var file = ReadPresetFile(); file.SetValue("favorites", "art", string.Join(",", _favorites)); WritePresetFile(file);
    }
    private void LoadBrushPresets()
    {
        if (_presets == null) return;
        using var file = ReadPresetFile(); _presets.Clear();
        foreach (string section in file.GetSections())
        {
            if (!section.StartsWith("brush:")) continue;
            // ED6: the preset's own name first, then what it paints, name before number.
            uint art = (uint)file.GetValue(section, "art", 0).AsInt64();
            int id = (int)(art < EditorData.LandCount ? art : art - EditorData.LandCount);
            int row = _presets.AddItem(art == 0 ? section[6..] : $"{section[6..]}  ({NameThenId(art, id)})");
            _presets.SetItemMetadata(row, section[6..]);
        }
        _favorites.Clear();
        foreach (string id in file.GetValue("favorites", "art", "").AsString().Split(',')) if (uint.TryParse(id, out uint n)) _favorites.Add(n);
        RebuildFavorites();
    }
    private void SaveBrushPreset()
    {
        string name = _presetName.Text.Trim();
        if (name.Length == 0) { _status.Text = "Give the brush a name first"; return; }
        try
        {
            using var file = ReadPresetFile(); string section = "brush:" + name;
            foreach (var n in _numbers) file.SetValue(section, n.Key, n.Value.Value);
            foreach (var c in _checks) file.SetValue(section, c.Key, c.Value.ButtonPressed);
            file.SetValue(section, "operation", _operation.Selected); file.SetValue(section, "target", _targetPick.Selected);
            file.SetValue(section, "variants", _weights.Text); file.SetValue(section, "allowed", _allowed.Text); file.SetValue(section, "edges", _edges.Text);
            file.SetValue(section, "art", (long)(_data?.CurrentArt ?? 0)); file.SetValue(section, "hue", (int)BrushHue);
            WritePresetFile(file); LoadBrushPresets(); _status.Text = $"Saved brush: {name}";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private string PresetName(int row) => _presets.GetItemMetadata(row).AsString();
    internal void SavePresetForSmoke(string name) { _presetName.Text = name; SaveBrushPreset(); }
    internal void SetVariantsForSmoke(string text) { _weights.Text = text; _recipe.Variants = text; RebuildVariants(); }
    internal void SetZHueForSmoke() { _stackIndex = -1; TransformStackSelection(); }
    internal IEnumerable<string> StackRowsForSmoke() => Enumerable.Range(0, _stack.ItemCount).Select(i => _stack.GetItemText(i));
    internal void ChooseStackRowForSmoke(int row) { _stack.Select(row); _stack.EmitSignal(ItemList.SignalName.ItemSelected, row); }
    internal void ShowInspectorTabForSmoke() { _pinNearby.ButtonPressed = false; _detailTabs.CurrentTab = 0; }
    internal IEnumerable<string> PresetRowsForSmoke() => Enumerable.Range(0, _presets.ItemCount).Select(i => _presets.GetItemText(i));
    internal IEnumerable<string> VariantRowsForSmoke() => _variantRows.GetChildren().OfType<HBoxContainer>().Select(r => r.GetChildren().OfType<Label>().First().Text);
    private void ReadPreset(string name)
    {
        using var file = ReadPresetFile(); string section = "brush:" + name;
        foreach (var n in _numbers) n.Value.Value = file.GetValue(section, n.Key, n.Value.Value).AsDouble();
        foreach (var c in _checks) c.Value.ButtonPressed = file.GetValue(section, c.Key, c.Value.ButtonPressed).AsBool();
        _operation.Select(Math.Clamp(file.GetValue(section, "operation", 0).AsInt32(), 0, _operation.ItemCount - 1));
        _recipe.Operation = _operation.GetItemText(_operation.Selected);
        _targetPick.Select(Math.Clamp(file.GetValue(section, "target", 0).AsInt32(), 0, 1)); _recipe.Land = _targetPick.Selected == 1;
        _weights.Text = file.GetValue(section, "variants", "").AsString(); _recipe.Variants = _weights.Text;
        _allowed.Text = file.GetValue(section, "allowed", "").AsString(); _recipe.AllowedLand = _allowed.Text;
        _edges.Text = file.GetValue(section, "edges", "").AsString(); _recipe.Edges = _edges.Text;
        if (_data != null) _data.CurrentArt = (uint)file.GetValue(section, "art", 0).AsInt64();
        BrushHue = (ushort)file.GetValue(section, "hue", 0).AsInt32(); _presetName.Text = name; Tool = WorldTool.Brush;
        RebuildVariants();
    }
}
#endif
