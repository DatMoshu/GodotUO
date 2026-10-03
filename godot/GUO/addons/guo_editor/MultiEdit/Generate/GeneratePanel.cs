#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The multi editor's "Generate" side panel: a styles picker, a generator, its sliders, a seed and Reroll.
/// Every change regenerates (after a short debounce) and raises <see cref="Generated"/> with the components;
/// the canvas tab decides what to do with them (preview as a ghost, accept as an edit). It shows only the
/// controls its generator takes. Self-contained: it uses a <see cref="MultiGenerateClient"/> (its own, or the one
/// the view shares) and nothing else in the editor.
/// </summary>
[Tool]
public sealed partial class GeneratePanel : VBoxContainer
{
    private readonly MultiGenerateClient _client;
    private readonly bool _owns;
    private readonly Random _dice = new();
    private OptionButton _style;
    private OptionButton _generator;
    private OptionButton _shape;
    private OptionButton _roof;
    private OptionButton _roofKind;
    private OptionButton _stairKind;
    private OptionButton _turn;
    private CheckBox _door;
    private CheckBox _windows;
    private CheckBox _parapet;
    private CheckBox _closed;
    private SpinBox _seed;
    private readonly Dictionary<string, HSlider> _sliders = new();
    private readonly Dictionary<string, Label> _values = new();
    private readonly Dictionary<string, List<Control>> _groups = new();
    private Label _status;
    private Godot.Timer _debounce;
    private List<GenStyle> _styles = new();
    private long _ticket;
    private TaskCompletionSource _stylesLoaded = new();

    private static readonly string[] Generators = { "house", "autowall", "roof", "stairs" };
    private static readonly string[] Shapes = { "rect", "L", "T", "U", "cross" };
    private static readonly string[] Roofs = { "gable", "hip", "flat", "none" };
    private static readonly string[] RoofKinds = { "gable", "hip", "flat" };
    private static readonly string[] StairKinds = { "straight", "turned", "ladder" };

    /// <summary>Raised with each fresh result (op name, result). Check <see cref="GenResult.Ok"/>.</summary>
    public event Action<string, GenResult> Generated;

    /// <summary>Raised when the generator choice changes, so the canvas can supply that generator's place.</summary>
    public event Action<string> GeneratorChanged;

    /// <summary>Raised by the Apply buttons: true replaces the multi, false adds to it.</summary>
    public event Action<bool> ApplyRequested;

    /// <summary>Raised by "Clear preview".</summary>
    public event Action PreviewCleared;

    /// <summary>Completes once the styles are listed (the smoke check awaits it).</summary>
    public Task StylesLoaded => _stylesLoaded.Task;

    public int StyleCount => _styles.Count;

    /// <summary>The generator's last answer.</summary>
    public GenResult Last { get; private set; }

    /// <summary>
    /// Parameters the canvas supplies for the generators that need a place: autowall's "path", roof's "boxes",
    /// stairs' "at" and "rise". They are merged into every request except the house's, as given.
    /// </summary>
    public JsonObject Context { get; set; } = new();

    public GeneratePanel() : this(null)
    {
    }

    public GeneratePanel(MultiGenerateClient client)
    {
        _owns = client == null;
        _client = client ?? new MultiGenerateClient();
        Name = "Generate";
    }

    public override void _Ready()
    {
        if (_style != null)
        {
            return;
        }

        AddChild(new Label { Text = "Generate (live preview, then Apply)" });
        _style = Row("Style", new OptionButton());
        _generator = Row("Generator", new OptionButton());
        foreach (string g in Generators)
        {
            _generator.AddItem(g);
        }

        _shape = Group("house", Row("Shape", new OptionButton()));
        foreach (string s in Shapes)
        {
            _shape.AddItem(s);
        }

        _roof = Group("house", Row("Roof", new OptionButton()));
        foreach (string s in Roofs)
        {
            _roof.AddItem(s);
        }

        Slider("house", "width", "Width", 8, 40, 14);
        Slider("house", "depth", "Depth", 8, 40, 12);
        Slider("house", "storeys", "Storeys", 1, 3, 1);
        Slider("house", "rooms", "Rooms", 1, 12, 3);
        Slider("house", "window_every", "Window every", 2, 6, 3);

        _door = Check("autowall", "Door", true);
        _windows = Check("autowall", "Windows", true);
        _closed = Check("autowall", "Closed loop", false);
        Slider("autowall", "wall_storeys", "Storeys", 1, 3, 1);
        Slider("autowall", "wall_window_every", "Window every", 2, 6, 3);

        _roofKind = Group("roof", Row("Kind", new OptionButton()));
        foreach (string s in RoofKinds)
        {
            _roofKind.AddItem(s);
        }

        _parapet = Check("roof", "Parapet (flat)", false);

        _stairKind = Group("stairs", Row("Kind", new OptionButton()));
        foreach (string s in StairKinds)
        {
            _stairKind.AddItem(s);
        }

        _turn = Group("stairs", Row("Turn", new OptionButton()));
        _turn.AddItem("left");
        _turn.AddItem("right");
        Slider("stairs", "stair_width", "Width", 1, 4, 1);
        Slider("stairs", "steps", "Steps", 2, 8, 4);

        _seed = Group("house", Row("Seed", new SpinBox { MinValue = 0, MaxValue = 999999, Value = 1, Rounded = true }));
        var reroll = new Button { Text = "Reroll", TooltipText = "A new seed" };
        reroll.Pressed += () =>
        {
            _seed.Value = _dice.Next(1, 999999);
            Regenerate();
        };
        AddChild(reroll);
        Group("house", reroll);

        var apply = new HBoxContainer();
        var replace = new Button { Text = "Apply (replace)", TooltipText = "Replace the multi with this, one undo step" };
        replace.Pressed += () => ApplyRequested?.Invoke(true);
        var add = new Button { Text = "Apply (add)", TooltipText = "Add this to the multi, one undo step" };
        add.Pressed += () => ApplyRequested?.Invoke(false);
        var clear = new Button { Text = "Clear preview" };
        clear.Pressed += () => PreviewCleared?.Invoke();
        apply.AddChild(replace);
        apply.AddChild(add);
        apply.AddChild(clear);
        AddChild(apply);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) };
        AddChild(_status);

        _debounce = new Godot.Timer { OneShot = true, WaitTime = 0.12 };
        _debounce.Timeout += Regenerate;
        AddChild(_debounce);
        foreach (OptionButton o in new[] { _style, _shape, _roof, _roofKind, _stairKind, _turn })
        {
            o.ItemSelected += _ => Changed();
        }

        _generator.ItemSelected += _ =>
        {
            ShowGroups();
            GeneratorChanged?.Invoke(Generator);
            Changed();
        };
        foreach (CheckBox c in new[] { _door, _windows, _parapet, _closed })
        {
            c.Toggled += _ => Changed();
        }

        _seed.ValueChanged += _ => Changed();
        ShowGroups();
        LoadStyles();
    }

    private T Group<T>(string group, T control) where T : Control
    {
        if (!_groups.TryGetValue(group, out List<Control> list))
        {
            _groups[group] = list = new List<Control>();
        }

        // A row's control sits in an HBox that the Row call added; hide the row, not just the control.
        list.Add(control.GetParent() is HBoxContainer row && row.GetParent() == this ? row : control);
        return control;
    }

    private void ShowGroups()
    {
        string g = Generator;
        foreach ((string key, List<Control> list) in _groups)
        {
            foreach (Control c in list)
            {
                c.Visible = key == g;
            }
        }
    }

    private T Row<T>(string label, T control) where T : Control
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(control);
        AddChild(row);
        return control;
    }

    private CheckBox Check(string group, string text, bool on)
    {
        var c = new CheckBox { Text = text, ButtonPressed = on };
        AddChild(c);
        Group(group, c);
        return c;
    }

    private void Slider(string group, string key, string label, int min, int max, int value)
    {
        var s = new HSlider { MinValue = min, MaxValue = max, Step = 1, Value = value, CustomMinimumSize = new Vector2(120, 0) };
        var v = new Label { Text = value.ToString(), CustomMinimumSize = new Vector2(24, 0) };
        s.ValueChanged += x =>
        {
            v.Text = ((int)x).ToString();
            Changed();
        };
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(s);
        row.AddChild(v);
        AddChild(row);
        Group(group, s);
        _sliders[key] = s;
        _values[key] = v;
    }

    private async void LoadStyles()
    {
        _styles = await _client.StylesAsync();
        _style.Clear();
        foreach (GenStyle s in _styles)
        {
            _style.AddItem($"{s.Name}");
        }

        _status.Text = _styles.Count == 0 ? (_client.Why ?? "no styles found") : $"{_styles.Count} styles";
        _stylesLoaded.TrySetResult();
        if (_styles.Count > 0)
        {
            Regenerate();
        }
    }

    private void Changed() => _debounce?.Start();

    /// <summary>Picks a generator by name (the canvas tools do this) and tells the listeners.</summary>
    public void SelectGenerator(string name)
    {
        int i = Array.IndexOf(Generators, name);
        if (i < 0 || _generator == null || _generator.Selected == i)
        {
            return;
        }

        _generator.Select(i);
        ShowGroups();
        GeneratorChanged?.Invoke(name);
        Changed();
    }

    /// <summary>Picks a style by key.</summary>
    public void SelectStyle(string key)
    {
        int i = _styles.FindIndex(s => s.Key == key);
        if (i >= 0)
        {
            _style.Select(i);
            Changed();
        }
    }

    public IReadOnlyList<GenStyle> Styles => _styles;

    /// <summary>Shows a line in the panel's status (what the active tool needs).</summary>
    public void SetHint(string text)
    {
        if (_status != null)
        {
            _status.Text = text;
        }
    }

    public string StyleKey => _styles.Count > 0 ? _styles[Math.Max(0, _style.Selected)].Key : "";

    /// <summary>The request the current controls describe, for the selected generator.</summary>
    public JsonObject Request()
    {
        var r = new JsonObject { ["style"] = StyleKey };
        switch (Generator)
        {
            case "house":
                r["seed"] = (int)_seed.Value;
                r["shape"] = Shapes[Math.Max(0, _shape.Selected)];
                r["roof"] = Roofs[Math.Max(0, _roof.Selected)];
                foreach (string k in new[] { "width", "depth", "storeys", "rooms", "window_every" })
                {
                    r[k] = (int)_sliders[k].Value;
                }

                return r;
            case "autowall":
                r["storeys"] = (int)_sliders["wall_storeys"].Value;
                r["window_every"] = (int)_sliders["wall_window_every"].Value;
                r["windows"] = _windows.ButtonPressed;
                r["door"] = _door.ButtonPressed ? new JsonObject { ["index"] = "middle" } : JsonValue.Create(false);
                r["closed"] = _closed.ButtonPressed;
                break;
            case "roof":
                r["kind"] = RoofKinds[Math.Max(0, _roofKind.Selected)];
                r["parapet"] = _parapet.ButtonPressed;
                break;
            case "stairs":
                r["kind"] = StairKinds[Math.Max(0, _stairKind.Selected)];
                r["turn"] = _turn.Selected == 1 ? "right" : "left";
                r["width"] = (int)_sliders["stair_width"].Value;
                r["steps"] = (int)_sliders["steps"].Value;
                break;
        }

        foreach ((string k, JsonNode v) in Context)
        {
            r[k] = v?.DeepClone();
        }

        return r;
    }

    public string Generator => Generators[Math.Max(0, _generator?.Selected ?? 0)];

    /// <summary>Generates now and raises <see cref="Generated"/> on the main thread. Stale answers are dropped.</summary>
    public async void Regenerate() => await NowAsync();

    /// <summary>Generates now, skipping the debounce, and returns the answer (the Apply buttons wait for this).</summary>
    public async Task<GenResult> NowAsync()
    {
        _debounce?.Stop();
        if (_styles.Count == 0)
        {
            return Last;
        }

        long mine = ++_ticket;
        string op = Generator;
        GenResult res = await _client.GenerateAsync(op, Request());
        if (mine != _ticket || !IsInsideTree())
        {
            return Last;
        }

        Last = res;
        _status.Text = res.Ok
            ? $"{res.Components.Count} components, {res.Ms:0} ms" +
              (res.Problems.Count > 0 ? $"\n{res.Problems.Count} problem(s): {res.Problems[0]}" : "") +
              (res.Notes.Count > 0 ? $"\n{res.Notes[0]}" : "")
            : res.Error;
        Generated?.Invoke(op, res);
        return res;
    }

    public override void _ExitTree()
    {
        if (_owns)
        {
            _client.Dispose();
        }
    }
}
#endif
