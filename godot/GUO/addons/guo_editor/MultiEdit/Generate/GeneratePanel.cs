#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;

/// <summary>
/// The multi editor's "Generate" side panel: a styles picker, a generator, its sliders, a seed and Reroll.
/// Every change regenerates (after a short debounce) and raises <see cref="Generated"/> with the components;
/// the canvas tab decides what to do with them (preview as a ghost, accept as an edit). Self-contained: it owns a
/// <see cref="MultiGenerateClient"/> and nothing else in the editor.
/// </summary>
public sealed partial class GeneratePanel : VBoxContainer
{
    private readonly MultiGenerateClient _client;
    private readonly Random _dice = new();
    private OptionButton _style;
    private OptionButton _generator;
    private OptionButton _shape;
    private OptionButton _roof;
    private SpinBox _seed;
    private readonly Dictionary<string, HSlider> _sliders = new();
    private readonly Dictionary<string, Label> _values = new();
    private Label _status;
    private Godot.Timer _debounce;
    private List<GenStyle> _styles = new();
    private long _ticket;

    private static readonly string[] Generators = { "house", "autowall", "roof", "stairs" };
    private static readonly string[] Shapes = { "rect", "L", "T", "U", "cross" };
    private static readonly string[] Roofs = { "gable", "hip", "flat", "none" };

    /// <summary>Raised with each fresh result (op name, result). Check <see cref="GenResult.Ok"/>.</summary>
    public event Action<string, GenResult> Generated;

    /// <summary>
    /// Parameters the canvas supplies for the generators that need a place: autowall's "path", roof's "boxes",
    /// stairs' "at" and "rise". They are merged into the request as given.
    /// </summary>
    public JsonObject Context { get; set; } = new();

    public GeneratePanel(MultiGenerateClient client = null)
    {
        _client = client ?? new MultiGenerateClient();
        Name = "Generate";
    }

    public override void _Ready()
    {
        AddChild(new Label { Text = "Generate" });
        _style = Row("Style", new OptionButton());
        _generator = Row("Generator", new OptionButton());
        foreach (string g in Generators)
        {
            _generator.AddItem(g);
        }

        _shape = Row("Shape", new OptionButton());
        foreach (string s in Shapes)
        {
            _shape.AddItem(s);
        }

        _roof = Row("Roof", new OptionButton());
        foreach (string s in Roofs)
        {
            _roof.AddItem(s);
        }

        Slider("width", "Width", 8, 40, 14);
        Slider("depth", "Depth", 8, 40, 12);
        Slider("storeys", "Storeys", 1, 3, 1);
        Slider("rooms", "Rooms", 1, 12, 3);
        Slider("window_every", "Window every", 2, 6, 3);
        _seed = Row("Seed", new SpinBox { MinValue = 0, MaxValue = 999999, Value = 1, Rounded = true });
        var reroll = new Button { Text = "Reroll" };
        reroll.Pressed += () =>
        {
            _seed.Value = _dice.Next(1, 999999);
            Regenerate();
        };
        AddChild(reroll);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) };
        AddChild(_status);

        _debounce = new Godot.Timer { OneShot = true, WaitTime = 0.12 };
        _debounce.Timeout += Regenerate;
        AddChild(_debounce);
        foreach (OptionButton o in new[] { _style, _generator, _shape, _roof })
        {
            o.ItemSelected += _ => Changed();
        }

        _seed.ValueChanged += _ => Changed();
        LoadStyles();
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

    private void Slider(string key, string label, int min, int max, int value)
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
        if (_styles.Count > 0)
        {
            Regenerate();
        }
    }

    private void Changed() => _debounce?.Start();

    /// <summary>The request the current controls describe.</summary>
    public JsonObject Request()
    {
        var r = new JsonObject
        {
            ["style"] = _styles.Count > 0 ? _styles[Math.Max(0, _style.Selected)].Key : "",
            ["seed"] = (int)_seed.Value,
            ["shape"] = Shapes[Math.Max(0, _shape.Selected)],
            ["roof"] = Roofs[Math.Max(0, _roof.Selected)],
        };
        foreach ((string k, HSlider s) in _sliders)
        {
            r[k] = (int)s.Value;
        }

        foreach ((string k, JsonNode v) in Context)
        {
            r[k] = v?.DeepClone();
        }

        return r;
    }

    public string Generator => Generators[Math.Max(0, _generator.Selected)];

    /// <summary>Generates now and raises <see cref="Generated"/> on the main thread. Stale answers are dropped.</summary>
    public async void Regenerate()
    {
        if (_styles.Count == 0)
        {
            return;
        }

        long mine = ++_ticket;
        string op = Generator;
        GenResult res = await _client.GenerateAsync(op, Request());
        if (mine != _ticket || !IsInsideTree())
        {
            return;
        }

        _status.Text = res.Ok
            ? $"{res.Components.Count} components, {res.Ms:0} ms" +
              (res.Problems.Count > 0 ? $"\n{res.Problems.Count} problem(s): {res.Problems[0]}" : "") +
              (res.Notes.Count > 0 ? $"\n{res.Notes[0]}" : "")
            : res.Error;
        Generated?.Invoke(op, res);
    }

    public override void _ExitTree() => _client.Dispose();
}
#endif
