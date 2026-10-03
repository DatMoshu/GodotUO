#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The UO Inspector: shows whatever an Assets panel picked (an
/// <see cref="Inspection"/>): a still or an animation, its text, and the
/// panel's buttons. The "Inspector" of docs/editor_plan.md §3; later phases
/// feed it map cells, statics and spawners from the World tab too.
/// </summary>
[Tool]
public partial class InspectorDock : EditorDock
{
    private TextureRect _preview;
    private OptionButton _zoom;
    private HBoxContainer _player;
    private Button _play;
    private HSlider _frame;
    private HFlowContainer _actions;
    private RichTextLabel _fields;

    private double _clock;
    private bool _playing;

    /// <summary>What is on show, or null.</summary>
    public Inspection Current { get; private set; }

    /// <summary>The texture on show; null when the inspection has no image.</summary>
    public Texture2D Texture => _preview?.Texture;

    public InspectorDock()
    {
        Name = "UOInspector";
        Title = "UO Inspector";
        LayoutKey = "guo_inspector";
        DefaultSlot = DockSlot.RightUl;
        AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;
        IconName = "Search";
    }

    public override void _Ready()
    {
        if (_preview != null)
        {
            return;
        }

        float scale = EditorInterface.Singleton.GetEditorScale();
        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        // Top: the preview, which keeps its pixel size and scrolls (a multi or
        // a zoomed radar is bigger than the dock; scaling it would resample).
        var top = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 220 * scale),
            SizeFlagsStretchRatio = 1.1f,
        };
        root.AddChild(top);

        var row = new HBoxContainer();
        top.AddChild(row);
        row.AddChild(new Label { Text = "Zoom" });
        _zoom = new OptionButton();
        foreach (int z in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            _zoom.AddItem($"{z}x");
        }

        _zoom.Selected = 1;
        _zoom.ItemSelected += _ => ShowFrame((int)_frame.Value);
        row.AddChild(_zoom);

        _player = new HBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_player);
        _play = new Button { Text = "Pause" };
        _play.Pressed += () =>
        {
            _playing = !_playing;
            _play.Text = _playing ? "Pause" : "Play";
        };
        _player.AddChild(_play);
        _frame = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, Step = 1 };
        _frame.ValueChanged += v => ShowFrame((int)v);
        _player.AddChild(_frame);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        top.AddChild(scroll);
        var centre = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        scroll.AddChild(centre);
        _preview = new TextureRect
        {
            StretchMode = TextureRect.StretchModeEnum.Keep,
            ExpandMode = TextureRect.ExpandModeEnum.KeepSize,
            // Never filter pixel art (AGENTS.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
        };
        centre.AddChild(_preview);

        // Bottom: buttons (wrapping) and the details, scrolling, so nothing is cut off.
        var detailScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 160 * scale),
            SizeFlagsStretchRatio = 1f,
        };
        root.AddChild(detailScroll);
        var details = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        detailScroll.AddChild(details);

        _actions = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        details.AddChild(_actions);

        _fields = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SelectionEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = "Pick something in the UO Assets tab.",
        };
        details.AddChild(_fields);
    }

    public void ShowInspection(Inspection inspection)
    {
        if (_preview == null)
        {
            _Ready();
        }

        Current = inspection;
        _fields.Text = inspection.Text;

        foreach (Node child in _actions.GetChildren())
        {
            child.QueueFree();
        }

        foreach (var (label, run) in inspection.Actions)
        {
            var b = new Button { Text = label };
            b.Pressed += () => run();
            _actions.AddChild(b);
        }

        int frames = inspection.Frames.Length;
        _player.Visible = frames > 1;
        _frame.MaxValue = System.Math.Max(0, frames - 1);
        _frame.SetValueNoSignal(0);
        _playing = frames > 1;
        _play.Text = "Pause";
        _clock = 0;
        ShowFrame(0);
    }

    private void ShowFrame(int index)
    {
        Image img = Current != null && index < Current.Frames.Length ? Current.Frames[index] : null;
        if (img == null)
        {
            _preview.Texture = null;
            return;
        }

        // KeepCentered draws at native size; zoom scales a copy with nearest
        // neighbour so it stays pixel art.
        int z = _zoom.Selected + 1;
        if (z > 1)
        {
            img = (Image)img.Duplicate();
            img.Resize(img.GetWidth() * z, img.GetHeight() * z, Image.Interpolation.Nearest);
        }

        _preview.Texture = ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        if (!_playing || Current == null || Current.Frames.Length < 2)
        {
            return;
        }

        _clock += delta;
        double step = 1.0 / System.Math.Max(1, Current.Fps);
        if (_clock >= step)
        {
            _clock -= step;
            _frame.Value = ((int)_frame.Value + 1) % Current.Frames.Length;
        }
    }
}
#endif
