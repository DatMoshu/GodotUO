#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using System.Globalization;
using System.Text;
using Godot;
using GUO.Game.GameObjects;

/// <summary>
/// The UO World main-screen tab (docs/editor_plan.md §4.4): the game's own
/// renderer in a viewport, read only. Go to a facet and cell, pan with the
/// arrow keys or a right/middle drag, zoom with the wheel, click to inspect
/// what the game's picking finds under the pointer. The Maps panel's radar
/// jumps here.
/// </summary>
[Tool]
public partial class WorldView : VBoxContainer
{
    private readonly EditorData _data;
    private readonly WorldHost _host = new();
    private readonly WorldEditor _editor;
    private WorldGuides _guides;
    private WorldModes _modeNode;
    private LegendChip _chip;
    private MenuButton _viewMenu, _layerMenu, _layersAgain;
    private Label _cursor;
    private readonly MapLayers _mapLayers;
    private OptionButton _tool;
    private AssetField _hue;
    private AssetField _brush;
    private OptionButton _season;

    /// <summary>The world's season (the game's own seasonal graphics).</summary>
    internal GUO.Game.Managers.Season Season
    {
        get => _host.Season;
        set
        {
            _host.Season = value;
            _season?.Select((int)value);
        }
    }

    private OptionButton _facet;
    private LineEdit _coords;
    private Label _status;
    private SubViewportContainer _container;
    private Control _stage;
    private MiniMap _minimap;
    private MenuButton _layers, _guideMenu;

    /// <summary>Where the minimap gets a facet's radar image; set by the plugin (the Maps tab renders it).</summary>
    public Func<int, Image> RadarSource { get; set; }

    /// <summary>The minimap, for the smoke check and the tour.</summary>
    internal MiniMap Minimap => _minimap;

    /// <summary>The Layers and Guides menus' buttons.</summary>
    public MenuButton LayersMenu => _layers;
    public MenuButton GuidesMenu => _guideMenu;

    /// <summary>Sets a layer or guide item (by its label) in the Layers or Guides menu, as a click on it would. False if there is none.</summary>
    public bool SetMenuItem(string label, bool on)
    {
        foreach (MenuButton m in new[] { _layers, _guideMenu })
        {
            PopupMenu pm = m?.GetPopup();
            for (int i = 0; pm != null && i < pm.ItemCount; i++)
            {
                if (pm.GetItemText(i) == label)
                {
                    if (pm.IsItemChecked(i) != on)
                    {
                        pm.EmitSignal(PopupMenu.SignalName.IdPressed, pm.GetItemId(i));
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a Layers or Guides item is checked.</summary>
    public bool MenuItemChecked(string label)
    {
        foreach (MenuButton m in new[] { _layers, _guideMenu })
        {
            PopupMenu pm = m?.GetPopup();
            for (int i = 0; pm != null && i < pm.ItemCount; i++)
            {
                if (pm.GetItemText(i) == label)
                {
                    return pm.IsItemChecked(i);
                }
            }
        }

        return false;
    }
    private SubViewport _viewport;
    private Node2D _canvas;

    private Vector2 _drag;
    private bool _dragging;
    private Vector2 _rightPress;
    private bool _rightDown;
    private (int facet, int x, int y) _pending = (0, 1496, 1628);

    /// <summary>Raised with what a click picked.</summary>
    public event Action<Inspection> Inspect;

    /// <summary>What the map context menu can make from an Area selection.</summary>
    public enum NewZoneKind
    {
        Music,
        Sfx,
        Shard,
    }

    /// <summary>Fired by the map context menu: kind, facet and inclusive rect.</summary>
    public Action<NewZoneKind, int, int, int, int, int> NewZoneFromArea;

    public bool IsBooted => _host.IsBooted;
    public string Error => _host.Error ?? _data?.Error;
    internal WorldHost Host => _host;
    internal WorldEditor Editor => _editor;

    /// <summary>The world-objects layer (ADR-0014): spawners and placed items.</summary>
    internal ObjectLayer Objects => _objects;

    private readonly ObjectLayer _objects;
    private LineEdit _spawnEntry;

    /// <summary>The object MoveObject picked up and will put down on the next click.</summary>
    private Guid? _moving;
    public WorldGuides Guides => _guides;

    /// <summary>The render modes' host (ADR-0027), for the smoke check, the tour and F3.</summary>
    internal WorldModes Modes => _modeNode;



    /// <summary>The map layers drawn on this view and on the minimap.</summary>
    internal MapLayers Layers => _mapLayers;

    internal LegendChip Chip => _chip;
    internal MenuButton ViewMenu => _viewMenu;
    internal MenuButton MapLayersMenu => _layerMenu;

    /// <summary>The tool a left click uses.</summary>
    public WorldTool Tool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            if (_tool != null)
            {
                _tool.Selected = (int)value;
            }
            SyncToolButtons();
        }
    }

    /// <summary>The hue the Stamp and Hue tools apply.</summary>
    public ushort BrushHue
    {
        get => (ushort)(_hue?.Value ?? 0);
        set
        {
            if (_hue != null)
            {
                _hue.Value = value;
            }
        }
    }

    /// <summary>When set, the pointer position the game's picking uses, instead of the real one (smoke check).</summary>
    public Vector2I? ForcedMouse { get; set; }
    internal Vector2I CanvasSize => _viewport?.Size ?? Vector2I.Zero;

    public WorldView() : this(null)
    {
    }

    public WorldView(EditorData data)
    {
        _data = data;
        if (_data != null)
        {
            _data.AssetsApplied += OnAssetsApplied;
            _data.Loaded += OnEditorDataLoaded;
        }

        _objects = new ObjectLayer(_host);
        _objects.Changed += what =>
        {
            UpdateStatus();
            _status.Text = what;
        };
        _mapLayers = new MapLayers(_host)
        {
            ProjectRoot = () => _host.Project?.Root,
            Objects = () => _objects.Objects,
        };
        _editor = new WorldEditor(_host);
        _editor.Changed += what =>
        {
            _modeNode?.Invalidate();
            UpdateStatus();
            _status.Text = $"{what}   (undo {_editor.UndoCount}, redo {_editor.RedoCount})";
        };
        Name = "UOWorld";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // Not Visible = false here: an assembly reload recreates this object
        // through the parameterless constructor, and a visibility change then
        // fires handlers the old assembly connected. The plugin hides it.
    }

    public override void _Ready()
    {
        if (_viewport != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _facet = new OptionButton();
        for (int f = 0; f < 6; f++)
        {
            _facet.AddItem($"map{f}", f);
        }

        _facet.ItemSelected += _ => GoTo(_facet.GetItemId(_facet.Selected), _host.X, _host.Y);
        bar.AddChild(_facet);

        _coords = new LineEdit { PlaceholderText = "x,y", CustomMinimumSize = new Vector2(140, 0) };
        _coords.TextSubmitted += OnCoords;
        bar.AddChild(_coords);
        var go = new Button { Text = "Go" };
        go.Pressed += () => OnCoords(_coords.Text);
        bar.AddChild(go);

        var overlay = new Button
        {
            Text = "Reload project",
            TooltipText = "Re-read the world project's blocks from disk and lay them over the map again",
        };
        overlay.Pressed += () => ReloadOverlay();
        bar.AddChild(overlay);

        // A second, always-visible Layers menu: the original lives in the
        // toolbar below (and moves with workspace layouts), which left
        // people with no clickable Layers in some arrangements. A real
        // MenuButton, so the engine positions its popup on the right
        // monitor; its items drive the original menu through SetToggle.
        _layersAgain = new MenuButton
        {
            Text = "Layers",
            TooltipText = "What the world draws: land, statics, multis, roofs, world objects, live",
        };
        _layersAgain.GetPopup().HideOnCheckableItemSelection = false;
        bar.AddChild(_layersAgain);

        _status = new Label
        {
            Text = "the world starts the first time this tab is shown",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        bar.AddChild(_status);

        // Second row: tools, brush, history, layers.
        var tools = new HBoxContainer();
        AddChild(tools);
        _legacyTools = tools;
        _tool = new OptionButton { TooltipText = "What a left click does" };
        foreach (string t in Enum.GetNames<WorldTool>())
        {
            _tool.AddItem(t);
        }

        tools.AddChild(_tool);
        _tool.ItemSelected += i => Tool = (WorldTool)(int)i;
        tools.AddChild(new Label { Text = "static" });
        _brush = new AssetField(_data, AssetPickKind.Static)
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            CustomMinimumSize = new Vector2(190 * EditorInterface.Singleton.GetEditorScale(), 0),
            Placeholder = "name or id",
            TooltipText = "What Stamp places: type a name (torch) or an id (0x0A0F), pick in UO Assets, or drag a static here",
        };
        _brush.Committed += id =>
        {
            if (_data != null)
            {
                _data.CurrentArt = EditorData.LandCount + (uint)id;
            }
        };
        tools.AddChild(_brush);
        tools.AddChild(new Label { Text = "hue" });
        _hue = new AssetField(_data, AssetPickKind.Hue)
        {
            AllowZero = true,
            SizeFlagsHorizontal = SizeFlags.Fill,
            CustomMinimumSize = new Vector2(150 * EditorInterface.Singleton.GetEditorScale(), 0),
            Placeholder = "0",
            TooltipText = "Hue for Stamp and Hue: a name or an id; 0 for none",
        };
        tools.AddChild(_hue);
        var undo = new Button { Text = "Undo", TooltipText = "Ctrl+Z" };
        undo.Pressed += () => _editor.Undo();
        tools.AddChild(undo);
        var redo = new Button { Text = "Redo", TooltipText = "Ctrl+Y" };
        redo.Pressed += () => _editor.Redo();
        tools.AddChild(redo);
        var areaToMulti = new Button { Text = "Area to multi", TooltipText = "Tool Area: click two corners, then take the statics inside into a new multi in the Multi Editor" };
        areaToMulti.Pressed += () => SaveAreaAsMulti();
        tools.AddChild(areaToMulti);
        tools.AddChild(new VSeparator());
        // One menu for the layers and one for the guides: the toolbar must fit
        // at 2560 px and below without pushing the inspector off screen.
        _layers = Menu(tools, "Layers", "What the world draws: land, statics, multis, roofs, world objects");
        MenuToggle(_layers, "Land", true, v => _host.ShowLand = v);
        MenuToggle(_layers, "Statics", true, v => _host.ShowStatics = v);
        MenuToggle(_layers, "Multis", true, v => _host.ShowMultis = v);
        MenuToggle(_layers, "Roofs", true, v => _host.ShowRoofs = v);
        MenuToggle(_layers, "Objects", true, v => _objects.Visible = v);
        MenuToggle(_layers, "Live", false, v => SetLayer("Live", v));
        MenuToggle(_layers, "Live players", true, v => SetLiveKinds(v, _mapLayers.Live.Mobiles));
        MenuToggle(_layers, "Live mobiles", true, v => SetLiveKinds(_mapLayers.Live.Players, v));
        MirrorLayersMenu();
        tools.AddChild(new Label { Text = "spawns" });
        _spawnEntry = new LineEdit
        {
            Text = "Horse",
            CustomMinimumSize = new Vector2(110, 0),
            TooltipText = "What PlaceSpawner spawns: a creature or vendor class name on the shard (Horse, Tanner, ...)",
        };
        tools.AddChild(_spawnEntry);
        _season = new OptionButton { TooltipText = "Season: a shard sends one per map; pick the one it uses" };
        foreach (string s in Enum.GetNames<GUO.Game.Managers.Season>())
        {
            _season.AddItem(s);
        }

        _season.Selected = (int)GUO.Game.Managers.Season.Summer;
        _season.ItemSelected += i => _host.Season = (GUO.Game.Managers.Season)(int)i;
        tools.AddChild(_season);
        tools.AddChild(new VSeparator());
        _guideMenu = Menu(tools, "Guides", "Editor-only guides: cell grid, altitude numbers, block boundaries, the minimap");
        MenuToggle(_guideMenu, "Grid", false, v => _guides.Grid = v);
        MenuToggle(_guideMenu, "Altitude", false, v => _guides.Altitude = v);
        MenuToggle(_guideMenu, "Blocks", false, v => _guides.Blocks = v);
        MenuToggle(_guideMenu, "Minimap", true, v => _minimap.Visible = v);

        BuildModeRow();

        _stage = new Control
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddChild(_stage);

        _container = new SubViewportContainer
        {
            Stretch = true,
            FocusMode = FocusModeEnum.All,
            // Pixel art is never filtered (AGENTS.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _container.GuiInput += OnInput;
        _stage.AddChild(_container);
        _container.SetAnchorsPreset(LayoutPreset.FullRect);

        // The minimap: a radar around the camera in a corner of the view.
        _minimap = new MiniMap { Name = "MiniMap" };
        _minimap.Jump += (x, y) => GoTo(_host.Facet, x, y);
        _minimap.Layers = _mapLayers;
        _stage.AddChild(_minimap);

        _viewport = new SubViewport
        {
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            HandleInputLocally = false,
            TransparentBg = false,
        };
        _container.AddChild(_viewport);

        _canvas = new Node2D { Name = "WorldCanvas", TextureFilter = TextureFilterEnum.Nearest };
        _viewport.AddChild(_canvas);

        // After the canvas, so the guides draw over the game's frame.
        _guides = new WorldGuides { Name = "Guides" };
        _guides.Attach(_host);
        _viewport.AddChild(_guides);

        // After the guides: the active render mode and the map layers (ADR-0027).
        _modeNode = new WorldModes { Name = "Modes" };
        _modeNode.Attach(_host);
        _modeNode.Layers = _mapLayers;
        _viewport.AddChild(_modeNode);

        _chip = new LegendChip { Name = "Legend", Visible = false, Position = new Vector2(10, 10) };
        _stage.AddChild(_chip);

        BuildWorkspace(bar, tools);

        VisibilityChanged += OnVisibilityChanged;
    }

    private void OnVisibilityChanged()
    {
        if (!Visible) { _painting = false; _stroke.Clear(); _spacePan = false; }
        if (Visible)
        {
            EnsureBooted();
        }
    }

    /// <summary>
    /// The world view's own pixels (its SubViewport): what the camera draws,
    /// in the camera's coordinates. Null when the view is not up. The Layers
    /// dock's area capture reads this, never the editor window: the window
    /// also holds UI, and its coordinates do not match the camera's.
    /// </summary>
    public Image CaptureView()
    {
        if (_viewport == null || !IsVisibleInTree())
        {
            return null;
        }

        return _viewport.GetTexture().GetImage();
    }

    /// <summary>Starts the world if it has not started. False, with <see cref="Error"/>, if it cannot.</summary>
    public bool EnsureBooted()
    {
        if (_host.IsBooted)
        {
            return true;
        }

        if (_viewport == null)
        {
            _Ready();
        }

        if (_data == null || !_data.IsLoaded)
        {
            _status.Text = _data?.Error ?? "waiting for editor data...";
            return false;
        }

        _status.Text = "starting the world...";
        if (!_host.Boot(_canvas, _pending.facet, _pending.x, _pending.y, _data))
        {
            _status.Text = $"could not start: {_host.Error}";
            return false;
        }

        // The world project (ADR-0011): whole replaced blocks over the
        // install, from UO_WORLD_PROJECT.
        try
        {
            string root = EditorData.Setting("UO_WORLD_PROJECT", "");
            if (root.Length == 0)
            {
                root = System.IO.Path.Combine(EditorData.RepoRoot, "build", "world", "default");
            }

            OpenProject(root);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] world project: {ex.GetType().Name}: {ex.Message}");
        }

        // The project's replaced art, gumps and hues (ADR-0020), on the
        // world's own loaders, after editor data has loaded. OnAssetsApplied
        // also catches later imports and reverts.
        if (_data?.Assets != null)
        {
            _host.ApplyAssets(_data.Assets);
        }

        UpdateStatus();
        return true;
    }

    private void OnEditorDataLoaded()
    {
        if (IsInstanceValid(this) && Visible)
        {
            EnsureBooted();
        }
    }

    private void OnAssetsApplied()
    {
        _ghostTextures.Clear(); _previewArt = uint.MaxValue;
        if (IsInstanceValid(this) && _host.World != null && _data?.Assets != null)
        {
            _host.ApplyAssets(_data.Assets);
        }
    }

    /// <summary>Opens a world project (creating it if needed) and forgets the old one's undo history.</summary>
    public WorldProject OpenProject(string root)
    {
        _stackIndex = -1;
        _editor.Clear();
        _stackCell = null;
        _moving = null;
        WorldProject project = _host.OpenProject(root);
        _mapLayers.ProjectChanged();
        _modeNode?.Invalidate();
        _objects.Open(project?.Root);
        LoadBrushPresets();
        UpdateStatus();
        return project;
    }

    /// <summary>Re-reads the world project from disk and lays it over the map again.</summary>
    public int ReloadOverlay()
    {
        int n = _host.ApplyOverlay();
        UpdateStatus();
        return n;
    }

    /// <summary>Moves the view; starts the world first if needed.</summary>
    public bool GoTo(int facet, int x, int y)
    {
        _stackIndex = -1;
        _pending = (facet, x, y);
        if (!EnsureBooted())
        {
            return false;
        }

        int before = _host.Facet;
        bool ok = _host.GoTo(facet, x, y);
        if (_host.Facet != before)
        {
            _objects.Redraw();
        }

        UpdateStatus();
        return ok;
    }

    private void OnCoords(string text)
    {
        string[] p = (text ?? "").Split(',', ' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 2
            && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            GoTo(_host.IsBooted ? _host.Facet : _pending.facet, x, y);
        }
    }

    private void UpdateStatus()
    {
        if (!_host.IsBooted)
        {
            return;
        }

        _facet.Select(_facet.GetItemIndex(_host.Facet));
        _coords.Text = $"{_host.X},{_host.Y}";
        string project = _host.Project != null ? $"project {_host.Project.Name} ({_host.Project.Blocks(_host.Facet).Count} blocks here)   " : "";
        _status.Text = $"{project}map{_host.Facet} {_host.X},{_host.Y} z {_host.Z}   zoom {_host.Scene.Camera.Zoom:0.0}   "
            + $"{_host.Scene.RenderedObjectsCount} objects   (arrows / right-drag pan, wheel zoom, click inspects)";
    }

    public override void _Process(double delta)
    {
        if (!Visible || !_host.IsBooted || _viewport == null)
        {
            return;
        }

        Vector2I size = _viewport.Size;
        if (_data != null && _brush != null && _data.CurrentArt >= EditorData.LandCount)
        {
            // Follow a pick made in UO Assets, but never under the user's typing.
            int id = (int)(_data.CurrentArt - EditorData.LandCount);
            if (_brush.Value != id && _brush.Edit?.HasFocus() != true)
            {
                _brush.Value = id;
            }
        }

        if (_minimap != null && _minimap.Visible)
        {
            _minimap.Update(_host, RadarSource, size, _host.Scene.Camera.Zoom);
        }

        _guides.Hover = Tool != WorldTool.Select && _host.Picked is GameObject hover ? (hover.X, hover.Y) : null;
        UpdateWorkspace(delta);
        UpdateModeUi();
        Vector2 local = _container.GetLocalMousePosition();
        Vector2I? mouse = ForcedMouse ?? (new Rect2(Vector2.Zero, _container.Size).HasPoint(local)
            ? new Vector2I((int)local.X, (int)local.Y)
            : null);
        CellGeometry liveGeo = _mapLayers.Live.On && mouse != null ? CellGeometry.From(_host) : null;
        _container.TooltipText = liveGeo != null
            ? _mapLayers.Live.Hover(liveGeo.Facet, mouse.Value, liveGeo.Project)
            : "";

        try
        {
            _host.Draw(_canvas, size, mouse);
        }
        catch (Exception ex)
        {
            _status.Text = $"draw failed: {ex.GetType().Name}: {ex.Message}";
            GD.PrintErr($"[GUO editor] world draw: {ex}");
            SetProcess(false);
        }
    }

    private void OnInput(InputEvent e)
    {
        if (!_host.IsBooted)
        {
            return;
        }

        if (WorkspaceInput(e)) { _container.AcceptEvent(); return; }

        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _host.Scene.Camera.ZoomIn();
                UpdateStatus();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _host.Scene.Camera.ZoomOut();
                UpdateStatus();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right or MouseButton.Middle } b:
                _dragging = b.Pressed;
                _drag = Vector2.Zero;
                if (b.ButtonIndex == MouseButton.Right)
                {
                    if (b.Pressed)
                    {
                        _rightDown = true;
                        _rightPress = b.Position;
                    }
                    else if (_rightDown)
                    {
                        _rightDown = false;
                        // A release that barely moved is a click, not a pan.
                        if ((b.Position - _rightPress).Length() < 8f)
                        {
                            ShowMapMenu();
                        }
                    }
                }

                break;
            case InputEventMouseMotion m when _dragging:
                Pan(m.Relative);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } lb:
                _container.GrabFocus();
                if (Tool == WorldTool.Select)
                {
                    if (_modeNode.ModeName == "Reachability" && _host.Picked is GameObject at)
                    {
                        _modeNode.SetOrigin(at.X, at.Y, StandZ(at));
                    }

                    InspectPicked();
                }
                else
                {
                    ApplyTool(lb.ShiftPressed);
                }

                break;
            case InputEventKey { Pressed: true, CtrlPressed: true, Keycode: Key.Z } z:
                if (z.ShiftPressed)
                {
                    _editor.Redo();
                }
                else
                {
                    _editor.Undo();
                }

                _container.AcceptEvent();
                break;
            case InputEventKey { Pressed: true, CtrlPressed: true, Keycode: Key.Y }:
                _editor.Redo();
                _container.AcceptEvent();
                break;
            case InputEventKey { Pressed: true } k:
                int dx = 0, dy = 0;
                switch (k.Keycode)
                {
                    case Key.Up: dx = -1; dy = -1; break;
                    case Key.Down: dx = 1; dy = 1; break;
                    case Key.Left: dx = -1; dy = 1; break;
                    case Key.Right: dx = 1; dy = -1; break;
                }

                if (dx != 0 || dy != 0)
                {
                    int step = k.ShiftPressed ? 8 : 1;
                    GoTo(_host.Facet, _host.X + dx * step, _host.Y + dy * step);
                    _container.AcceptEvent();
                }

                break;
        }
    }

    /// <summary>
    /// A drag moves the world with the pointer. UO's diamond: a cell is
    /// 44 pixels wide and 44 high, x runs down-right and y down-left, so a
    /// screen offset (sx, sy) is x = (sx + sy) / 44 and y = (sy - sx) / 44.
    /// </summary>
    private void Pan(Vector2 relative)
    {
        float zoom = _host.Scene.Camera.Zoom;
        _drag -= relative * zoom;
        int dx = (int)((_drag.X + _drag.Y) / 44f);
        int dy = (int)((_drag.Y - _drag.X) / 44f);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        _drag -= new Vector2((dx - dy) * 22f, (dx + dy) * 22f);
        GoTo(_host.Facet, _host.X + dx, _host.Y + dy);
    }

    private static MenuButton Menu(HBoxContainer bar, string text, string tip)
    {
        var m = new MenuButton { Text = text, TooltipText = tip, Flat = false };
        bar.AddChild(m);
        // Stay open while ticking several items.
        m.GetPopup().HideOnCheckableItemSelection = false;
        return m;
    }

    /// <summary>
    /// Right-click without dragging: what to make of the Area selection, if
    /// any. Music/SFX zones land in their manifests (tracks come later);
    /// shard regions go to the Regions dock for naming and saving.
    /// </summary>
    private void ShowMapMenu()
    {
        bool hasArea = Area != null;
        var pm = new PopupMenu();
        // On the editor base control, not on this view: WorldView is a
        // layout container and would arrange a child popup at its top.
        EditorInterface.Singleton.GetBaseControl().AddChild(pm);
        pm.AddItem(hasArea ? "New Music zone from area" : "New Music zone (Area tool: click two corners first)", 0);
        pm.AddItem(hasArea ? "New SFX zone from area" : "New SFX zone (Area tool: click two corners first)", 1);
        pm.AddItem(hasArea ? "New Shard region from area" : "New Shard region (Area tool: click two corners first)", 2);
        if (!hasArea)
        {
            for (int i = 0; i < 3; i++)
            {
                pm.SetItemDisabled(i, true);
            }
        }

        pm.IdPressed += id =>
        {
            if (Area is { } a)
            {
                NewZoneFromArea?.Invoke((NewZoneKind)(int)id, _host.Facet,
                    Math.Min(a.X0, a.X1), Math.Min(a.Y0, a.Y1),
                    Math.Max(a.X0, a.X1), Math.Max(a.Y0, a.Y1));
            }

            pm.Hide();
        };
        pm.PopupHide += () => pm.QueueFree();
        pm.Popup(new Rect2I((Vector2I)GetGlobalMousePosition(), new Vector2I(10, 10)));
    }

    private readonly System.Collections.Generic.Dictionary<string, (PopupMenu Menu, int Id, Action<bool> Set)> _toggles = new();

    private void MenuToggle(MenuButton menu, string text, bool on, Action<bool> set)
    {
        PopupMenu pm = menu.GetPopup();
        int id = pm.ItemCount;
        pm.AddCheckItem(text, id);
        pm.SetItemChecked(id, on);
        _toggles[text] = (pm, id, set);
        pm.IdPressed += i =>
        {
            if (i != id)
            {
                return;
            }

            bool now = !pm.IsItemChecked(id);
            pm.SetItemChecked(id, now);
            set(now);
        };
    }

    /// <summary>
    /// Copies the Layers menu's items into the first-row duplicate, keeping
    /// checks in sync both ways: presses in the copy drive the original
    /// through SetToggle, and either popup refreshes the copy on open.
    /// </summary>
    private void MirrorLayersMenu()
    {
        if (_layersAgain == null || _layers == null)
        {
            return;
        }

        PopupMenu src = _layers.GetPopup();
        PopupMenu dst = _layersAgain.GetPopup();
        dst.Clear();
        for (int s = 0; s < src.ItemCount; s++)
        {
            string text = src.GetItemText(s);
            int id = dst.ItemCount;
            dst.AddCheckItem(text, id);
            dst.SetItemChecked(id, src.IsItemChecked(s));
        }

        dst.AboutToPopup += SyncLayersMirror;
        dst.IdPressed += pressed =>
        {
            string text = dst.GetItemText((int)pressed);
            bool? now = GetToggle(text);
            if (now == null)
            {
                return;
            }

            if (SetToggle(text, !now.Value))
            {
                dst.SetItemChecked((int)pressed, !now.Value);
            }
        };
        src.IdPressed += _ => SyncLayersMirror();
    }

    /// <summary>Copies the Layers menu's check states into the first-row duplicate.</summary>
    private void SyncLayersMirror()
    {
        if (_layersAgain == null || _layers == null)
        {
            return;
        }

        PopupMenu src = _layers.GetPopup();
        PopupMenu dst = _layersAgain.GetPopup();
        for (int d = 0; d < dst.ItemCount; d++)
        {
            string text = dst.GetItemText(d);
            bool? now = GetToggle(text);
            if (now != null)
            {
                dst.SetItemChecked(d, now.Value);
            }
        }
    }

    /// <summary>The Layers and Guides menu items by name (Land, Statics, Multis, Roofs, Objects, Grid, Altitude, Blocks, ...), for F3.</summary>
    internal System.Collections.Generic.IReadOnlyList<string> ToggleNames => new System.Collections.Generic.List<string>(_toggles.Keys);

    /// <summary>A layer or guide's state; null when there is no such toggle (or the tab has not been built).</summary>
    internal bool? GetToggle(string name) => _toggles.TryGetValue(name, out var t) ? t.Menu.IsItemChecked(t.Id) : null;

    /// <summary>Sets a layer or guide through its menu item, so the menu and the world agree.</summary>
    internal bool SetToggle(string name, bool on)
    {
        if (!_toggles.TryGetValue(name, out var t))
        {
            return false;
        }

        t.Menu.SetItemChecked(t.Id, on);
        t.Set(on);
        return true;
    }

    /// <summary>Fired by the PickRegion tool: facet and cell that was clicked.</summary>
    public Action<int, int, int> RegionClick;

    /// <summary>
    /// The current tool on what the game's picking found under the pointer.
    /// Returns a line saying what happened.
    /// </summary>
    public string ApplyTool(bool big = false)
    {
        if (EffectivePicked() is not GameObject o)
        {
            return _status.Text = "nothing under the pointer";
        }

        int facet = _host.Facet;
        bool done;
        switch (Tool)
        {
            case WorldTool.Stamp:
            {
                uint art = _data?.CurrentArt ?? 0;
                if (art < EditorData.LandCount)
                {
                    return _status.Text = "Stamp needs a static: pick one in UO Assets > Art (Statics)";
                }

                // On top of what was clicked: a static's top, or the land.
                sbyte z = _recipe.FixedHeight ? (sbyte)_recipe.Height : o is Static st ? (sbyte)Math.Min(127, st.Z + st.ItemData.Height) : o.Z;
                var stampCell = BrushCell(o);
                done = _editor.Stamp(facet, stampCell.X, stampCell.Y, z, (ushort)(art - EditorData.LandCount), BrushHue);
                break;
            }

            case WorldTool.Erase:
                if (o is not Static es)
                {
                    return _status.Text = $"Erase takes a static; that is a {o.GetType().Name}";
                }

                done = _editor.Erase(facet, es.X, es.Y, es.Z, es.Graphic);
                break;

            case WorldTool.Raise:
            case WorldTool.Lower:
                if (_lockTerrain?.ButtonPressed == true) return _status.Text = "Terrain is locked";
                int step = (big ? 5 : 1) * (Tool == WorldTool.Raise ? 1 : -1);
                done = _editor.Altitude(facet, o.X, o.Y, step);
                break;

            case WorldTool.Hue:
                if (o is not Static hs)
                {
                    return _status.Text = $"Hue takes a static; that is a {o.GetType().Name}";
                }

                done = _editor.SetHue(facet, hs.X, hs.Y, hs.Z, hs.Graphic, BrushHue);
                break;

            case WorldTool.PlaceItem:
            case WorldTool.PlaceSpawner:
            case WorldTool.MoveObject:
            case WorldTool.DeleteObject:
                return ApplyObjectTool(o);

            case WorldTool.Area:
                SetAreaCorner(o.X, o.Y);
                return _status.Text;

            case WorldTool.PickRegion:
                RegionClick?.Invoke(facet, o.X, o.Y);
                return _status.Text;

            case WorldTool.Measure:
            case WorldTool.Route:
            case WorldTool.Pin:
                return _status.Text = ApplyMapTool(o.X, o.Y, StandZ(o));

            default:
                InspectPicked();
                return "inspected";
        }

        if (!done)
        {
            string why = _host.Project == null ? "no world project is open" : "nothing changed";
            _status.Text = $"{Tool}: {why}";
            return why;
        }

        return _editor.LastWhat;
    }

    private (int X, int Y)? _areaA, _areaB;

    /// <summary>The rectangle the Area tool selected, corners in either order, or null.</summary>
    public (int X0, int Y0, int X1, int Y1)? Area =>
        _areaA is { } a && _areaB is { } b ? (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)) : null;

    /// <summary>Raised with a name and the parts when the area is taken into a multi (the plugin opens the Multi Editor).</summary>
    internal Action<string, System.Collections.Generic.List<MultiPart>> AreaToMulti { get; set; }

    /// <summary>The Area tool's click: the first sets a corner, the second the other, a third starts again.</summary>
    public void SetAreaCorner(int x, int y)
    {
        if (_areaA == null || _areaB != null)
        {
            _areaA = (x, y);
            _areaB = null;
            _status.Text = $"Area: corner {x},{y}; click the other corner";
        }
        else
        {
            _areaB = (x, y);
            (int X0, int Y0, int X1, int Y1) a = Area.Value;
            _status.Text = $"Area: {a.X1 - a.X0 + 1} x {a.Y1 - a.Y0 + 1} cells; 'Area to multi' takes its statics";
        }

        _guides.Area = Area ?? (_areaA is { } c ? (c.X, c.Y, c.X, c.Y) : null);
    }

    /// <summary>Sets both corners at once (the smoke check, F3).</summary>
    public void SetArea(int x0, int y0, int x1, int y1)
    {
        _areaA = (x0, y0);
        _areaB = (x1, y1);
        _guides.Area = Area;
    }

    /// <summary>
    /// The statics inside the area (the project's blocks first, else the map's), centred on the area and
    /// re-based on the land's z at its middle, as a multi's parts. The land itself is not taken.
    /// </summary>
    public System.Collections.Generic.List<MultiPart> AreaParts(out string name)
    {
        name = null;
        if (Area is not { } a || !_host.IsBooted)
        {
            return null;
        }

        int cx = (a.X0 + a.X1) / 2, cy = (a.Y0 + a.Y1) / 2;
        int baseZ = _host.World.Map.GetTileZ(cx, cy);
        name = $"world_{_host.Facet}_{a.X0}_{a.Y0}";
        return _editor.StaticsInRect(_host.Facet, a.X0, a.Y0, a.X1, a.Y1)
            .OrderBy(t => t.X).ThenBy(t => t.Y).ThenBy(t => t.S.Z)
            .Select(t => new MultiPart { Id = t.S.Id, X = (short)(t.X - cx), Y = (short)(t.Y - cy), Z = (short)(t.S.Z - baseZ), Shown = true, Hue = t.S.Hue })
            .ToList();
    }

    /// <summary>Takes the area's statics into a new multi in the Multi Editor (ADR-0031). Returns how many.</summary>
    public int SaveAreaAsMulti()
    {
        var parts = AreaParts(out string name);
        if (parts == null)
        {
            _status.Text = "Area to multi: pick two corners with the Area tool first";
            return 0;
        }

        if (parts.Count == 0)
        {
            _status.Text = "Area to multi: there are no statics in that rectangle";
            return 0;
        }

        AreaToMulti?.Invoke(name, parts);
        _status.Text = $"Area to multi: {parts.Count} statics into the Multi Editor";
        return parts.Count;
    }

    private const uint PreviewSerial = 0x4000_F001;

    /// <summary>
    /// Goes to a cell and draws one frame synchronously, then reports what the
    /// live scene hook drew for a staged splat. The smoke check: _Process only
    /// runs while this tab is visible, so a hidden world would never draw.
    /// </summary>
    internal (int Level, int Drawn) ProbeSplat(string name, int facet, int x, int y)
    {
        if (!EnsureBooted() || _canvas == null)
        {
            return (-2, 0);
        }

        GoTo(facet, x, y);
        Vector2I size = _viewport?.Size ?? new Vector2I(800, 600);
        _host.Draw(_canvas, size, null);
        return _host.Scene == null ? (-2, 0) : _host.Scene.SplatDrawn(name);
    }

    /// <summary>Places a multi (one the Multi Editor wrote to a stage, say) beside the view centre. False when the world is not up.</summary>
    public bool PreviewMulti(int id)
    {
        if (!_host.IsBooted)
        {
            return false;
        }

        _host.RemoveServerObject(PreviewSerial);
        int x = _host.X + 4, y = _host.Y;
        return _host.PlaceServerMulti(PreviewSerial, (ushort)id, (ushort)x, (ushort)y, _host.World.Map.GetTileZ(x, y)) != null;
    }

    /// <summary>The world-object tools (ADR-0014) on the picked object or cell.</summary>
    private string ApplyObjectTool(GameObject o)
    {
        if (_objects.Objects == null)
        {
            return _status.Text = "no world project is open";
        }

        int facet = _host.Facet;
        Guid? picked = _objects.IdOf(o);

        // On top of what was clicked, as Stamp does.
        sbyte z = o is Static st ? (sbyte)Math.Min(127, st.Z + st.ItemData.Height) : o.Z;
        switch (Tool)
        {
            case WorldTool.PlaceItem:
            {
                uint art = _data?.CurrentArt ?? 0;
                if (art < EditorData.LandCount)
                {
                    return _status.Text = "PlaceItem needs a static: pick one in UO Assets > Art (Statics)";
                }

                _objects.PlaceItem(facet, o.X, o.Y, z, (ushort)(art - EditorData.LandCount), BrushHue);
                break;
            }

            case WorldTool.PlaceSpawner:
                if (_objects.PlaceSpawner(facet, o.X, o.Y, z, _spawnEntry?.Text) == null)
                {
                    return _status.Text = "PlaceSpawner needs a name in the spawns box";
                }

                break;

            case WorldTool.MoveObject:
                if (_moving == null)
                {
                    if (picked == null)
                    {
                        return _status.Text = "MoveObject: click one of the project's objects first";
                    }

                    _moving = picked;
                    return _status.Text = "MoveObject: now click where it goes";
                }

                _objects.Move(_moving.Value, facet, o.X, o.Y, o is Item ? o.Z : z);
                _moving = null;
                break;

            case WorldTool.DeleteObject:
                if (picked == null || !_objects.Delete(picked.Value))
                {
                    return _status.Text = "DeleteObject takes one of the project's objects";
                }

                break;
        }

        return _status.Text;
    }

    /// <summary>Inspects what the game's picking found under the pointer on the last frame.</summary>
    public Inspection InspectPicked()
    {
        if (EffectivePicked() is not GameObject o)
        {
            _status.Text = "nothing under the pointer";
            return null;
        }

        bool land = o is Land;
        uint index = land ? o.Graphic : EditorData.LandCount + o.Graphic;
        var sb = new StringBuilder();
        sb.Append($"[b]{o.GetType().Name} 0x{o.Graphic:X4}[/b] at map{_host.Facet} {o.X},{o.Y} z {o.Z}\n");
        sb.Append($"block {o.X >> 3},{o.Y >> 3}   cell {o.X & 7},{o.Y & 7}\n");
        if (o.Hue != 0)
        {
            sb.Append($"hue 0x{o.Hue:X4}\n");
        }

        if (_data != null && _data.IsLoaded)
        {
            sb.Append($"name {_data.NameOf(index)}\n");
        }

        sb.Append("(picked by the game's own PixelPicker)\n");
        var inspection = Inspection.Still("World", $"{o.X},{o.Y}", _data?.ArtImage(index), sb.ToString());
        // Items hang their pixels in the static archive under the same
        // LandCount + graphic index, so they bind like statics. Mobiles do
        // not: their graphic is a body id, not static art.
        if (_data?.IsLoaded == true && o is Land or Static or Item)
        {
            _data.CurrentArt = index;
            AssetActions.Add(inspection, _data, land ? AssetKind.Land : AssetKind.Static, o.Graphic, inspection.Image);
        }
        Inspect?.Invoke(inspection);
        return inspection;
    }

    /// <summary>
    /// Pins the world viewport to an exact size (a parity shot matching the
    /// client's 600x480 world view), or back to filling the tab with null.
    /// </summary>
    public void SetFixedSize(Vector2I? size)
    {
        if (_container == null)
        {
            return;
        }

        _container.Stretch = size == null;
        if (size is { } s)
        {
            _container.SetAnchorsPreset(LayoutPreset.TopLeft);
            _container.CustomMinimumSize = new Vector2(s.X, s.Y);
            _container.Size = new Vector2(s.X, s.Y);
            _stage.CustomMinimumSize = new Vector2(s.X, s.Y);
            _stage.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            _stage.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            _viewport.Size = s;
        }
        else
        {
            _container.CustomMinimumSize = Vector2.Zero;
            _container.SetAnchorsPreset(LayoutPreset.FullRect);
            _stage.CustomMinimumSize = Vector2.Zero;
            _stage.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _stage.SizeFlagsVertical = SizeFlags.ExpandFill;
        }
    }

    /// <summary>The viewport's last frame, for the smoke check. Null headless.</summary>
    public Image Capture() => DisplayServer.GetName() == "headless" ? null : _viewport?.GetTexture()?.GetImage();

    public void Shutdown()
    {
        _brushArt?.Shutdown();
        if (_data != null) _data.Loaded -= OnBrushDataLoaded;
        SetProcess(false);
        VisibilityChanged -= OnVisibilityChanged;
        if (_container != null)
        {
            _container.GuiInput -= OnInput;
        }

        if (_data != null)
        {
            _data.AssetsApplied -= OnAssetsApplied;
            _data.Loaded -= OnEditorDataLoaded;
        }

        _modeNode?.Detach();
        _host.Dispose();
    }
}
#endif
