#if TOOLS
namespace GUO.Editor;

using System;
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

    private OptionButton _facet;
    private LineEdit _coords;
    private Label _status;
    private SubViewportContainer _container;
    private SubViewport _viewport;
    private Node2D _canvas;

    private Vector2 _drag;
    private bool _dragging;
    private (int facet, int x, int y) _pending = (0, 1496, 1628);

    /// <summary>Raised with what a click picked.</summary>
    public event Action<Inspection> Inspect;

    public bool IsBooted => _host.IsBooted;
    public string Error => _host.Error;
    internal WorldHost Host => _host;

    /// <summary>When set, the pointer position the game's picking uses, instead of the real one (smoke check).</summary>
    public Vector2I? ForcedMouse { get; set; }

    public WorldView() : this(null)
    {
    }

    public WorldView(EditorData data)
    {
        _data = data;
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

        _status = new Label
        {
            Text = "the world starts the first time this tab is shown",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        bar.AddChild(_status);

        _container = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All,
            // Pixel art is never filtered (CLAUDE.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _container.GuiInput += OnInput;
        AddChild(_container);

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

        VisibilityChanged += OnVisibilityChanged;
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
        {
            EnsureBooted();
        }
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

        _status.Text = "starting the world...";
        if (!_host.Boot(_canvas, _pending.facet, _pending.x, _pending.y))
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

            _host.OpenProject(root);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] world project: {ex.GetType().Name}: {ex.Message}");
        }

        UpdateStatus();
        return true;
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
        _pending = (facet, x, y);
        if (!EnsureBooted())
        {
            return false;
        }

        bool ok = _host.GoTo(facet, x, y);
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
        Vector2 local = _container.GetLocalMousePosition();
        Vector2I? mouse = ForcedMouse ?? (new Rect2(Vector2.Zero, _container.Size).HasPoint(local)
            ? new Vector2I((int)local.X, (int)local.Y)
            : null);

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
                break;
            case InputEventMouseMotion m when _dragging:
                Pan(m.Relative);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                _container.GrabFocus();
                InspectPicked();
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

    /// <summary>Inspects what the game's picking found under the pointer on the last frame.</summary>
    public Inspection InspectPicked()
    {
        if (_host.Picked is not GameObject o)
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
        Inspect?.Invoke(inspection);
        return inspection;
    }

    /// <summary>The viewport's last frame, for the smoke check. Null headless.</summary>
    public Image Capture() => DisplayServer.GetName() == "headless" ? null : _viewport?.GetTexture()?.GetImage();

    public void Shutdown()
    {
        SetProcess(false);
        VisibilityChanged -= OnVisibilityChanged;
        if (_container != null)
        {
            _container.GuiInput -= OnInput;
        }

        _host.Dispose();
    }
}
#endif
