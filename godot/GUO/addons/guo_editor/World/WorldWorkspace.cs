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
    private HBoxContainer _legacyTools, _legacyModes;
    private Control _library, _settings, _quickFavorites;
    private ArtPanel _brushArt;
    private readonly Dictionary<WorldTool, Button> _toolButtons = new();
    private readonly WorldBrush _recipe = new();
    private readonly HashSet<(int X, int Y)> _stroke = new();
    private bool _painting, _spacePan;
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
    private readonly List<(int X, int Y, sbyte Z, ushort Id, bool Land)> _stackEntries = new();
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
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, Step = 1, CustomMinimumSize = new Vector2(95, 0) };
        row.AddChild(spin); spin.ValueChanged += v => change((int)v); _numbers[label] = spin; return spin;
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
            (WorldTool.Hue, "ColorPick", "H"), (WorldTool.PlaceItem, "Node3D", ""), (WorldTool.PlaceSpawner, "MemberSignal", ""),
            (WorldTool.MoveObject, "ToolMove", ""), (WorldTool.DeleteObject, "Remove", ""),
            (WorldTool.Measure, "Ruler", "M"), (WorldTool.Route, "CurvePath", ""), (WorldTool.Pin, "Pin", ""), (WorldTool.Area, "RegionEdit", "G"),
        };
        foreach (var entry in icons)
        {
            Texture2D icon = EditorInterface.Singleton.GetBaseControl().GetThemeIcon(entry.Icon, "EditorIcons");
            var button = new Button { Icon = icon, Text = icon == null ? entry.Tool.ToString()[..1] : "", ToggleMode = true,
                ButtonGroup = group, CustomMinimumSize = new Vector2(38, 34), TooltipText = entry.Tool + (entry.Shortcut.Length > 0 ? " (" + entry.Shortcut + ")" : "") };
            button.Pressed += () => Tool = entry.Tool; rail.AddChild(button); _toolButtons[entry.Tool] = button;
        }

        var library = new VBoxContainer { CustomMinimumSize = new Vector2(290, 0) };
        body.AddChild(library); _library = library;
        library.AddChild(new Label { Text = "Brush library" });
        var recipes = new HFlowContainer(); library.AddChild(recipes);
        ActionButton(recipes, "Scatter", () => QuickRecipe(false, "Paint", 7, 35, 2));
        ActionButton(recipes, "Terrain", () => QuickRecipe(true, "Paint", 5, 100, 1));
        ActionButton(recipes, "Sculpt", () => QuickRecipe(true, "Raise", 5, 100, 1));
        _brushArt = new ArtPanel { MinimumGridHeight = 100, ShowNames = true, SizeFlagsVertical = SizeFlags.ExpandFill };
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
        _presets = new ItemList { CustomMinimumSize = new Vector2(0, 100), TooltipText = "Double-click a saved brush recipe to load it" };
        library.AddChild(_presets); _presets.ItemActivated += i => ReadPreset(_presets.GetItemText((int)i));
        _presetName = new LineEdit { PlaceholderText = "Name this brush preset" }; library.AddChild(_presetName);
        var presetActions = new HBoxContainer(); library.AddChild(presetActions);
        ActionButton(presetActions, "Save preset", SaveBrushPreset);
        ActionButton(presetActions, "Load", () => { var s = _presets.GetSelectedItems(); if (s.Length > 0) ReadPreset(_presets.GetItemText(s[0])); });

        _stage.Reparent(body);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(270, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll); _settings = scroll;
        var options = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(options);
        options.AddChild(new Label { Text = "Tool settings" });
        _brush.Reparent(options); _brush.ClipText = true; _brush.CustomMinimumSize = new Vector2(240, 0);
        _brushPreview = new TextureRect { CustomMinimumSize = new Vector2(0, 72), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
        options.AddChild(_brushPreview);
        var editors = new HBoxContainer(); options.AddChild(editors);
        ActionButton(editors, "Pixelorama", () => EditBrushArt(false), "Edit this asset; use GUO > Save back to GUO to return it");
        ActionButton(editors, "Pinta", () => EditBrushArt(true), "Edit this asset; save the exported PNG to return it");
        var brushSettings = Section(options, "Brush", true);
        _operation = new OptionButton();
        foreach (string op in new[] { "Paint", "Erase statics", "Hue statics", "Raise", "Lower", "Flatten", "Smooth" }) _operation.AddItem(op);
        _operation.ItemSelected += i => _recipe.Operation = _operation.GetItemText((int)i); brushSettings.AddChild(_operation);
        _targetPick = new OptionButton(); _targetPick.AddItem("Target: statics"); _targetPick.AddItem("Target: terrain");
        _targetPick.ItemSelected += i => { _recipe.Land = i == 1; _brushArt.SelectKind(i == 1); _brushArt.Search(""); };
        brushSettings.AddChild(_targetPick);
        Number(brushSettings, "Size (tiles)", 7, 1, 31, v => _recipe.Size = v);
        Check(brushSettings, "Square footprint", false, v => _recipe.Square = v);
        Number(brushSettings, "Density (%)", 35, 0, 100, v => _recipe.Density = v);
        Number(brushSettings, "Spacing (tiles)", 2, 1, 16, v => _recipe.Spacing = v);
        Number(brushSettings, "Strength", 1, 1, 32, v => _recipe.Strength = v);
        brushSettings.AddChild(new Label { Text = "Hue (decimal)" });
        _hue.Reparent(brushSettings); _hue.TooltipText = "Hue (decimal); applies to Stamp and brush painting/recoloring";
        _hue.ValueChanged += v => _recipe.Hue = (ushort)v;

        var variation = Section(options, "Variation & rules");
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

        var precision = Section(options, "Visibility & height", true);
        Check(precision, "Fixed placement plane", false, v => _recipe.FixedHeight = v);
        Number(precision, "Z / ground offset", 0, -128, 127, v => _recipe.Height = v);
        Number(precision, "Visible Z min", -128, -128, 127, v => _host.MinVisibleZ = v);
        Number(precision, "Visible Z max", 127, -128, 127, v => _host.MaxVisibleZ = v);
        Check(precision, "Ghost roofs", false, v => _host.GhostRoofs = v);
        _lockTerrain = Check(precision, "Lock terrain", false, _ => { });
        var visibility = new HBoxContainer(); precision.AddChild(visibility);
        _layers.Reparent(visibility); _guideMenu.Reparent(visibility);
        _stack = new ItemList { CustomMinimumSize = new Vector2(0, 105), TooltipText = "Click to select an object at this cell. Alt+wheel cycles. Shift-click the map to pin a different cell." };
        var stackSection = Section(options, "Under cursor (Alt+wheel)", true); stackSection.AddChild(_stack);
        _stack.ItemSelected += i => { _stackIndex = (int)i; InspectPicked(); };
        var editStack = new HBoxContainer(); stackSection.AddChild(editStack);
        ActionButton(editStack, "Set Z / hue", TransformStackSelection, "Move the selected static to the Z value above and apply the hue");
        ActionButton(editStack, "Pick", PickBrushFromWorld);

        var advanced = Section(options, "World & overlays");
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
        _historyLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var history = Section(options, "History"); history.AddChild(_historyLabel);

        // Status belongs below the canvas. Top remains one compact command row.
        _status.Reparent(this);
        _status.CustomMinimumSize = Vector2.Zero;
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }; bar.AddChild(spacer);
        ActionButton(bar, "Undo", () => _editor.Undo(), "Ctrl+Z");
        ActionButton(bar, "Redo", () => _editor.Redo(), "Ctrl+Y");
        ActionButton(bar, "Brushes", () => _library.Visible = !_library.Visible);
        ActionButton(bar, "Precision", () => { _library.Hide(); _settings.Show(); });
        ActionButton(bar, "Focus", ToggleWorkspaceFocus, "Tab: hide panels and Godot docks");
        _previewLabel = new Label { Position = new Vector2(12, 12), MouseFilter = MouseFilterEnum.Ignore };
        _stage.AddChild(_previewLabel);
        var quick = new PanelContainer { Visible = false, Position = new Vector2(24, 60) };
        _stage.AddChild(quick); _quickFavorites = quick;
        SyncToolButtons(); LoadBrushPresets();
    }

    private void OnBrushDataLoaded() { _brushArt?.OnDataLoaded(); RebuildFavorites(); RebuildVariants(); }
    internal bool TerrainLocked { get => _lockTerrain?.ButtonPressed == true; set { if (_lockTerrain != null) _lockTerrain.ButtonPressed = value; } }
    internal string BrushStatus => _status?.Text;
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
            row.AddChild(new Label { Text = _data?.NameOf(art) ?? v.Id.ToString("X4"), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill });
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
        _painting = false; _stroke.Clear(); _lastStroke = null;
    }

    private void ToggleWorkspaceFocus()
    {
        bool focus = _settings.Visible || _library.Visible;
        _settings.Visible = !focus; _library.Visible = !focus;
        EditorInterface.Singleton.SetDistractionFreeMode(focus);
        if (focus) FocusRequested?.Invoke();
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
            var b = ActionButton(_favoriteButtons, "", () => { _data.CurrentArt = id; Tool = WorldTool.Brush; }, $"0x{id:X4}: {_data?.NameOf(id)}");
            Image img = _data?.ArtImage(id);
            if (img != null) b.Icon = ImageTexture.CreateFromImage(img);
            b.ExpandIcon = true; b.CustomMinimumSize = new Vector2(38, 38); b.TextureFilter = TextureFilterEnum.Nearest;
        }
    }

    private void ShowQuickFavorites()
    {
        foreach (Node n in _quickFavorites.GetChildren()) { _quickFavorites.RemoveChild(n); n.QueueFree(); }
        var list = new VBoxContainer(); _quickFavorites.AddChild(list);
        list.AddChild(new Label { Text = "Quick favorites · Q / Esc to close" });
        foreach (uint id in _favorites)
            ActionButton(list, $"{_data.NameOf(id)} · {id:X4}", () => { _data.CurrentArt = id; _quickFavorites.Hide(); Tool = WorldTool.Brush; });
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
            _brush.Text = $"{(_previewArt < EditorData.LandCount ? "Land" : "Static")} · {_data.NameOf(_previewArt)}";
        }
        if (_workspaceClock < 0.10) return;
        _workspaceClock = 0;
        _historyLabel.Text = $"Undo {_editor.UndoCount} · Redo {_editor.RedoCount}\n{_editor.LastWhat}";
        if (_host.Picked is GameObject o && !_painting && _stackIndex < 0 && _stackCell != ((int)o.X, (int)o.Y)) RefreshStack(o.X, o.Y);
        _guides.BrushCells.Clear();
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
        if (Tool == WorldTool.Brush && _host.Picked is GameObject h && _modeNode?.Data != null)
        {
            var hoverCell = BrushCell(h);
            uint currentArt = _data?.CurrentArt ?? 0;
            ushort fallback = (ushort)(currentArt < EditorData.LandCount ? currentArt : currentArt - EditorData.LandCount);
            foreach (var c in _recipe.PlanCells(_modeNode.Data, _painting ? _stroke : _recipe.Footprint(hoverCell.X, hoverCell.Y), fallback))
            {
                _guides.BrushCells.Add(c);
                if (_recipe.Operation != "Paint" || _recipe.Land || _data?.IsLoaded != true || _guides.Ghosts.Count >= 128) continue;
                uint art = _data.CurrentArt;
                if (art < EditorData.LandCount) continue;
                ushort id = _recipe.Choose(c.X, c.Y, (ushort)(art - EditorData.LandCount));
                Texture2D texture = GhostTexture(EditorData.LandCount + id);
                if (texture != null) _guides.Ghosts.Add((c.X, c.Y, Math.Clamp((_recipe.FixedHeight ? 0 : _modeNode.Data.LandZ(c.X, c.Y)) + _recipe.Height, -128, 127), texture));
            }
            _guides.PlaneZ = _recipe.FixedHeight ? _recipe.Height : null;
            _previewLabel.Text = $"{_recipe.Operation}: {_guides.BrushCells.Count} cells · release to apply · Esc cancel";
        }
        else { _previewLabel.Text = "Alt: pick art · Space: pan · Q: favorites · Tab: focus"; _guides.PlaneZ = null; }
    }

    private bool WorkspaceInput(InputEvent e)
    {
        if (e is InputEventKey { Keycode: Key.Space } space) { _spacePan = space.Pressed; if (!space.Pressed) _dragging = false; return true; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } pan && _spacePan)
        { _dragging = pan.Pressed; _drag = Vector2.Zero; return true; }
        if (e is InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, AltPressed: false } key)
        {
            switch (key.Keycode)
            {
                case Key.Tab: ToggleWorkspaceFocus(); return true;
                case Key.Q: ShowQuickFavorites(); return true;
                case Key.Escape: _painting = false; _stroke.Clear(); _quickFavorites.Hide(); _stackIndex = -1; return true;
                case Key.V: Tool = WorldTool.Select; return true;
                case Key.B: Tool = WorldTool.Brush; return true;
                case Key.S: Tool = WorldTool.Stamp; return true;
                case Key.E: Tool = WorldTool.Erase; return true;
                case Key.R: Tool = WorldTool.Raise; return true;
                case Key.F: Tool = WorldTool.Lower; return true;
                case Key.H: Tool = WorldTool.Hue; return true;
                case Key.M: Tool = WorldTool.Measure; return true;
                case Key.G: Tool = WorldTool.Area; return true;
                case Key.Bracketleft: _numbers["Size (tiles)"].Value--; return true;
                case Key.Bracketright: _numbers["Size (tiles)"].Value++; return true;
            }
        }
        if (e is InputEventMouseButton { Pressed: true, AltPressed: true } alt)
        {
            if (alt.ButtonIndex == MouseButton.Left) { PickBrushFromWorld(); return true; }
            if (alt.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (_host.Picked is GameObject at && _stackIndex < 0) RefreshStack(at.X, at.Y);
                if (_stackEntries.Count > 0)
                {
                    _stackIndex = (_stackIndex + (alt.ButtonIndex == MouseButton.WheelUp ? 1 : _stackEntries.Count - 1) + _stackEntries.Count) % _stackEntries.Count;
                    _stack.Select(_stackIndex); InspectPicked();
                }
                return true;
            }
        }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
            && _host.Picked is GameObject clicked && _stackCell != ((int)clicked.X, (int)clicked.Y)) _stackIndex = -1;
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, ShiftPressed: true } && Tool == WorldTool.Select)
        { _stackIndex = -1; if (_host.Picked is GameObject at) RefreshStack(at.X, at.Y); InspectPicked(); return true; }
        if (Tool != WorldTool.Brush) return false;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            if (b.Pressed) { _painting = true; _stroke.Clear(); _lastStroke = null; ExtendStroke(); _container.GrabFocus(); }
            else if (_painting) { ExtendStroke(); _painting = false; CommitStroke(); }
            return true;
        }
        if (e is InputEventMouseMotion && _painting) { ExtendStroke(); return true; }
        return false;
    }

    private void ExtendStroke()
    {
        if (_host.Picked is not GameObject at) return;
        var end = BrushCell(at);
        var start = _lastStroke ?? end;
        int steps = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        for (int s = 0; s <= steps; s++)
        {
            int x = start.X + (end.X - start.X) * s / Math.Max(1, steps);
            int y = start.Y + (end.Y - start.Y) * s / Math.Max(1, steps);
            foreach (var c in _recipe.Footprint(x, y)) if (_stroke.Count < 8192) _stroke.Add(c);
        }
        _lastStroke = end;
    }

    private (int X, int Y) BrushCell(GameObject at)
    {
        if (_recipe.FixedHeight && CellGeometry.From(_host) is { } geometry)
        {
            Vector2 mouse = ForcedMouse is { } forced ? new Vector2(forced.X, forced.Y) : _container.GetLocalMousePosition();
            var c = geometry.CellAt(mouse, _recipe.Height);
            return ((int)Math.Floor(c.X), (int)Math.Floor(c.Y));
        }
        return (at.X, at.Y);
    }

    private void CommitStroke()
    {
        try
        {
            string invalid = _recipe.Validate(_data);
            if (invalid != null) { _status.Text = invalid; return; }
            bool terrain = (_recipe.Operation == "Paint" && _recipe.Land) || _recipe.Operation is "Raise" or "Lower" or "Flatten" or "Smooth";
            if (terrain && _lockTerrain.ButtonPressed) { _status.Text = "Terrain is locked"; return; }
            if (_recipe.Operation == "Paint" && (_data?.IsLoaded != true || (_data.CurrentArt < EditorData.LandCount) != _recipe.Land))
            { _status.Text = "Choose art matching the target: terrain or statics"; return; }
            uint art = _data?.CurrentArt ?? 0;
            ushort id = (ushort)(art < EditorData.LandCount ? art : art - EditorData.LandCount);
            bool changed = _recipe.Apply(_editor, _modeNode.Data, _host.Facet, _stroke, id);
            if (!changed) _status.Text = "No cells changed (check brush rules and target)";
            _stackIndex = -1; _stackCell = null;
        }
        catch (Exception ex) { _status.Text = $"Brush failed: {ex.Message}"; }
        finally { _stroke.Clear(); _lastStroke = null; }
    }

    public override void _Input(InputEvent e)
    {
        // Release is global so dragging out of the viewport cannot leave a live stroke behind.
        if (_painting && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        { _painting = false; CommitStroke(); }
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
        foreach (var s in _editor.StaticsAt(_host.Facet, x, y).OrderByDescending(s => s.Z))
        {
            if (s.Z < _host.MinVisibleZ || s.Z > _host.MaxVisibleZ) continue;
            _stackEntries.Add((x, y, s.Z, s.Id, false));
            _stack.AddItem($"{_data?.NameOf(EditorData.LandCount + s.Id)} {s.Id:X4} · Z {s.Z}");
        }
        BlockData b = _modeNode?.Data?.Block(x >> 3, y >> 3);
        if (b != null)
        {
            int i = (y & 7) * 8 + (x & 7);
            _stackEntries.Add((x, y, b.LandZ[i], b.LandId[i], true));
            _stack.AddItem($"Terrain {b.LandId[i]:X4} · Z {b.LandZ[i]}");
        }
    }

    private GameObject EffectivePicked()
    {
        if (_stackIndex < 0 || _stackIndex >= _stackEntries.Count) return _host.Picked as GameObject;
        var s = _stackEntries[_stackIndex];
        var chunk = _host.World.Map.GetChunk2(s.X >> 3, s.Y >> 3, true);
        for (GameObject o = chunk?.GetHeadObject(s.X & 7, s.Y & 7); o != null; o = o.TNext)
            if ((s.Land ? o is Land : o is Static) && o.Graphic == s.Id && o.Z == s.Z) return o;
        return null;
    }

    private void PickBrushFromWorld()
    {
        if (EffectivePicked() is not GameObject o || _data == null) return;
        _data.CurrentArt = o is Land ? o.Graphic : EditorData.LandCount + o.Graphic;
        BrushHue = o.Hue; _recipe.Land = o is Land; _targetPick.Select(_recipe.Land ? 1 : 0);
        _status.Text = $"Picked {o.Graphic:X4} · Z {o.Z}";
    }

    private void TransformStackSelection()
    {
        if (_stackIndex < 0 || _stackIndex >= _stackEntries.Count) return;
        var s = _stackEntries[_stackIndex];
        if (s.Land) { _status.Text = "Use the terrain Flatten brush to set ground height"; return; }
        _editor.Edit(_host.Facet, s.X >> 3, s.Y >> 3, b =>
        {
            int i = b.Statics.FindIndex(t => t.X == (s.X & 7) && t.Y == (s.Y & 7) && t.Id == s.Id && t.Z == s.Z);
            if (i < 0) return;
            var t = b.Statics[i]; t.Z = (sbyte)_recipe.Height; t.Hue = BrushHue; b.Statics[i] = t;
        }, $"Move {s.Id:X4} to Z {_recipe.Height}, hue {BrushHue}");
        _stackIndex = -1; RefreshStack(s.X, s.Y);
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
        foreach (string section in file.GetSections()) if (section.StartsWith("brush:")) _presets.AddItem(section[6..]);
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
