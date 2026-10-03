#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Map Generator main-screen tab (ADR-0030). The left column holds the preset, seed, size, six
/// quick knobs and the actions. The middle column has one group per generator pass, built from
/// <c>guo-mapgen schema</c>: an on/off toggle and a control per tunable, with reset, randomise
/// and lock. The right column shows the radar preview, a pass stepper, layers and the
/// Felucca-likeness card. Every run goes to a fresh folder under build/mapgen/runs. Export and
/// opening the result in UO World are separate buttons.
/// </summary>
[Tool]
public partial class MapGenView : VBoxContainer
{
    public const string TabName = "Map Generator";

    /// <summary>Opens a world project in the World tab and shows a cell there: (root, facet, x, y).</summary>
    public Action<string, int, int, int> OpenInWorld;

    // Quick knobs: one slider moves several tunables within their declared ranges. The owner's
    // mountain and road heights (Mountain Shape PeakZ/FootZ/MaxStep) are deliberately not on any knob.
    private static readonly (string Label, string Tip, (string Pass, string Key, double Sign)[] Targets)[] Knobs =
    {
        ("Landmass size", "Bigger, fewer continents to the right. Moves Noise Height's base frequency.",
            new[] { ("Noise Height", "BaseFrequency", -1.0) }),
        ("Mountains", "More mountain ranges to the right. Moves Biome Assign's mountain share and range strength; heights stay.",
            new[] { ("Biome Assign", "MountainMinLandFraction", 1.0), ("Biome Assign", "RangeStrength", 1.0) }),
        ("Rivers", "More river sources to the right. Moves River Carve's source count.",
            new[] { ("River Carve", "SourceCount", 1.0) }),
        ("Forest", "More forest in the grass/forest mosaic to the right. Moves Biome Assign's forest mosaic share.",
            new[] { ("Biome Assign", "ForestMosaicShare", 1.0) }),
        ("Climate (dry to wet)", "Wetter to the right: more forest, swamp and jungle, less desert. Moves the moisture bias.",
            new[] { ("Moisture & Climate", "MoistureBias", 1.0) }),
        ("Coast roughness", "Rougher, more broken coasts to the right. Moves Noise Height's gain.",
            new[] { ("Noise Height", "Gain", 1.0) }),
    };

    private sealed class Tunable
    {
        public string Pass, Key, Label, Tooltip, Type;
        public bool Editable;
        public JsonElement PresetValue;
        public double? Min, Max;
        public string[] Options;
        public Control Control;
        public CheckBox Lock;
        public Control Row;
    }

    private sealed class PassGroup
    {
        public string Name, Category;
        public bool PresetEnabled;
        public CheckBox Enabled;
        public VBoxContainer Body;
        public Control Root;
        public readonly List<Tunable> Tunables = new();
    }

    private readonly List<PassGroup> _passes = new();
    private readonly Dictionary<string, double> _knobPresetValues = new();
    private readonly List<HSlider> _knobSliders = new();
    private readonly List<(int Index, string Name, string File)> _steps = new();
    private readonly Random _random = new();

    private OptionButton _preset, _size, _layer;
    private readonly List<string> _presetValues = new();
    private SpinBox _seed;
    private CheckBox _fast, _stepPreviews;
    private Button _generate, _cancel, _export, _openWorld, _savePreset;
    private LineEdit _savePresetName, _filter;
    private VBoxContainer _passList;
    private TextureRect _preview;
    private HSlider _stepSlider;
    private Label _stepLabel, _status, _score, _cellLabel;
    private GridContainer _likeness;
    private RichTextLabel _log;

    private bool _busy;
    private string _lastRun, _lastWorld;
    private int _mapW, _mapH, _originX, _originY;
    private JsonElement? _schema;

    /// <summary>The schema the tab is showing (for the smoke check).</summary>
    public int PassGroupCount => _passes.Count;
    public int TunableControlCount => _passes.Sum(p => p.Tunables.Count);
    public string LastRun => _lastRun;

    public MapGenView()
    {
        Name = "MapGenView";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Visible = false;

        var title = new HBoxContainer();
        title.AddChild(new Label { Text = "UO Map Generator", ThemeTypeVariation = "HeaderLarge" });
        _status = new Label { Text = "", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        title.AddChild(_status);
        AddChild(title);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        split.AddChild(BuildLeft());
        var split2 = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(split2);
        split2.AddChild(BuildMiddle());
        split2.AddChild(BuildRight());
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && Visible && _schema == null && !_busy)
        {
            _ = LoadSchemaAsync(null);
        }
    }

    // ---------------------------------------------------------------- left column

    private Control BuildLeft()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(260, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(col);

        col.AddChild(Heading("Preset"));
        _preset = new OptionButton { TooltipText = "The starting point. felucca-stage18 is the measured Felucca-like candidate." };
        _preset.ItemSelected += i => _ = LoadSchemaAsync(_presetValues[(int)i]);
        col.AddChild(_preset);

        var save = new HBoxContainer();
        _savePresetName = new LineEdit { PlaceholderText = "name for this setup", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _savePreset = new Button { Text = "Save", Disabled = true, TooltipText = "Save the last run's settings as a preset of your own (in the generator data folder)." };
        _savePreset.Pressed += SavePreset;
        save.AddChild(_savePresetName);
        save.AddChild(_savePreset);
        col.AddChild(save);

        col.AddChild(Heading("Seed"));
        var seedRow = new HBoxContainer();
        _seed = new SpinBox { MinValue = 0, MaxValue = int.MaxValue, Step = 1, Rounded = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Value = 1234567 };
        var dice = new Button { Text = "Roll", TooltipText = "A new random seed" };
        dice.Pressed += () => _seed.Value = _random.Next(1, int.MaxValue);
        seedRow.AddChild(_seed);
        seedRow.AddChild(dice);
        col.AddChild(seedRow);

        col.AddChild(Heading("Size"));
        _size = new OptionButton { TooltipText = "Map size in tiles. The client's facets are 7168x4096 (Felucca, Trammel), 2304x1600 (Ilshenar), 2560x2048 (Malas), 1448x1448 (Tokuno)." };
        col.AddChild(_size);

        col.AddChild(Heading("Quick start"));
        for (int k = 0; k < Knobs.Length; k++)
        {
            int knob = k;
            col.AddChild(new Label { Text = Knobs[k].Label, TooltipText = Knobs[k].Tip, MouseFilter = MouseFilterEnum.Pass });
            var slider = new HSlider { MinValue = -1, MaxValue = 1, Step = 0.05, Value = 0, TooltipText = Knobs[k].Tip };
            slider.ValueChanged += v => ApplyKnob(knob, v);
            _knobSliders.Add(slider);
            col.AddChild(slider);
        }

        col.AddChild(Heading("Run"));
        _fast = new CheckBox { Text = "Fast preview (no roads, stamps, scatter)", TooltipText = "Switches off the heavy passes. The map's shape and biomes are the same; roads and trees are missing." };
        _stepPreviews = new CheckBox { Text = "Picture after every pass", ButtonPressed = true, TooltipText = "Lets the stepper show the map after each pass." };
        col.AddChild(_fast);
        col.AddChild(_stepPreviews);

        _generate = new Button { Text = "Generate", TooltipText = "Run the generator into a new folder under build/mapgen/runs." };
        _generate.Pressed += () => _ = GenerateAsync();
        _cancel = new Button { Text = "Cancel", Disabled = true };
        _cancel.Pressed += () => _cancelSource?.Cancel();
        _export = new Button { Text = "Export map files", Disabled = true, TooltipText = "Regenerate the last run, check it matches, write map0/staidx0/statics0.mul into the run's export folder and read every cell back. Also writes a world project for UO World." };
        _export.Pressed += () => _ = ExportAsync();
        _openWorld = new Button { Text = "Open in UO World", Disabled = true, TooltipText = "Open the exported map's world project in the UO World tab. Deploying to a shard is a separate step there." };
        _openWorld.Pressed += OpenWorld;
        foreach (Button b in new[] { _generate, _cancel, _export, _openWorld })
        {
            col.AddChild(b);
        }

        return scroll;
    }

    private CancellationTokenSource _cancelSource;

    // ---------------------------------------------------------------- middle column

    private Control BuildMiddle()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var bar = new HBoxContainer();
        _filter = new LineEdit { PlaceholderText = "Filter passes and settings", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _filter.TextChanged += ApplyFilter;
        var expand = new Button { Text = "Open all" };
        expand.Pressed += () => SetAllOpen(true);
        var collapse = new Button { Text = "Close all" };
        collapse.Pressed += () => SetAllOpen(false);
        var resetAll = new Button { Text = "Reset all", TooltipText = "Every pass back to the preset" };
        resetAll.Pressed += () => { foreach (PassGroup g in _passes) ResetPass(g); ResetKnobs(); };
        bar.AddChild(_filter);
        bar.AddChild(expand);
        bar.AddChild(collapse);
        bar.AddChild(resetAll);
        col.AddChild(bar);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _passList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_passList);
        col.AddChild(scroll);
        return col;
    }

    // ---------------------------------------------------------------- right column

    private Control BuildRight()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var bar = new HBoxContainer();
        bar.AddChild(new Label { Text = "Show" });
        _layer = new OptionButton();
        _layer.AddItem("Radar (client colours)");
        _layer.AddItem("Biomes");
        _layer.AddItem("Height");
        _layer.ItemSelected += _ => ShowFinal();
        bar.AddChild(_layer);
        _cellLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        bar.AddChild(_cellLabel);
        col.AddChild(bar);

        _preview = new TextureRect
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest, // rule 7: never filter pixel art
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = "Click: the cell under the mouse. After Export, a click shows that spot in UO World.",
        };
        _preview.GuiInput += OnPreviewInput;
        col.AddChild(_preview);

        var stepRow = new HBoxContainer();
        stepRow.AddChild(new Label { Text = "After pass" });
        _stepSlider = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, MinValue = 0, MaxValue = 0, Step = 1, Editable = false };
        _stepSlider.ValueChanged += v => ShowStep((int)v);
        stepRow.AddChild(_stepSlider);
        _stepLabel = new Label { CustomMinimumSize = new Vector2(150, 0) };
        stepRow.AddChild(_stepLabel);
        col.AddChild(stepRow);

        col.AddChild(Heading("Felucca-likeness"));
        _score = new Label { Text = "Generate a map to measure it." };
        col.AddChild(_score);
        _likeness = new GridContainer { Columns = 3 };
        col.AddChild(_likeness);

        _log = new RichTextLabel { CustomMinimumSize = new Vector2(0, 110), ScrollFollowing = true, SelectionEnabled = true, FitContent = false };
        col.AddChild(_log);
        return col;
    }

    // ---------------------------------------------------------------- schema → controls

    private async Task LoadSchemaAsync(string preset)
    {
        SetBusy(true, preset == null ? "Reading the generator ..." : $"Loading {Path.GetFileName(preset)} ...");
        var args = new List<string> { "schema" };
        if (preset != null)
        {
            args.Add("--preset");
            args.Add(preset);
        }

        (JsonElement? schema, string error) = await MapGenCli.QueryAsync(args);
        Callable.From(() =>
        {
            SetBusy(false, error.Length > 0 ? $"Generator: {error}" : "");
            if (schema is JsonElement s)
            {
                LoadSchema(s);
            }
        }).CallDeferred();
    }

    /// <summary>Builds the controls from a <c>schema</c> object (docs/data_formats.md §24).</summary>
    public void LoadSchema(JsonElement schema)
    {
        _schema = schema;
        string presetId = schema.GetProperty("preset").GetProperty("id").GetString();
        if (schema.GetProperty("preset").TryGetProperty("seed", out JsonElement seed) && seed.ValueKind == JsonValueKind.Number)
        {
            _seed.Value = seed.GetInt64();
        }

        FillPresets(schema, presetId);
        FillSizes(schema);

        foreach (Node child in _passList.GetChildren())
        {
            _passList.RemoveChild(child);
            child.QueueFree();
        }

        _passes.Clear();
        foreach (JsonElement p in schema.GetProperty("passes").EnumerateArray())
        {
            if (p.GetProperty("file_side_effects").GetBoolean())
            {
                continue; // never run from GUO: nothing to toggle
            }

            _passes.Add(BuildPass(p));
        }

        ResetKnobs();
        ApplyFilter(_filter.Text);
        Log($"[color=gray]{_passes.Count} passes, {TunableControlCount} settings, preset {presetId}[/color]");
    }

    private void FillPresets(JsonElement schema, string current)
    {
        _presetValues.Clear();
        _preset.Clear();
        foreach (JsonElement id in schema.GetProperty("presets").EnumerateArray())
        {
            _presetValues.Add(id.GetString());
            _preset.AddItem(id.GetString());
        }

        if (Directory.Exists(MapGenCli.UserPresetsDir))
        {
            foreach (string f in Directory.GetFiles(MapGenCli.UserPresetsDir, "*.preset.json").OrderBy(f => f))
            {
                _presetValues.Add(f);
                _preset.AddItem("mine: " + Path.GetFileName(f)[..^".preset.json".Length]);
            }
        }

        int sel = _presetValues.FindIndex(v => v == current || Path.GetFileName(v) == current + ".preset.json");
        _preset.Select(Math.Max(0, sel));
    }

    private void FillSizes(JsonElement schema)
    {
        string old = _size.ItemCount > 0 ? _size.GetItemText(_size.Selected) : "1024 x 1024";
        _size.Clear();
        foreach (JsonElement s in schema.GetProperty("sizes").EnumerateArray())
        {
            _size.AddItem($"{s[0].GetInt32()} x {s[1].GetInt32()}");
        }

        for (int i = 0; i < _size.ItemCount; i++)
        {
            if (_size.GetItemText(i) == old)
            {
                _size.Select(i);
            }
        }
    }

    private PassGroup BuildPass(JsonElement p)
    {
        var g = new PassGroup
        {
            Name = p.GetProperty("name").GetString(),
            Category = p.GetProperty("category").GetString(),
            PresetEnabled = p.GetProperty("enabled").GetBoolean(),
        };

        var box = new VBoxContainer();
        var head = new HBoxContainer();
        var fold = new Button { Text = "+", Flat = true, CustomMinimumSize = new Vector2(24, 0), TooltipText = "Show the settings" };
        g.Enabled = new CheckBox { Text = g.Name, ButtonPressed = g.PresetEnabled, TooltipText = $"{g.Category} pass. Untick to skip it." };
        var cat = new Label { Text = g.Category + (p.GetProperty("heavy").GetBoolean() ? " (heavy)" : ""), Modulate = new Color(1, 1, 1, 0.55f), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var reset = new Button { Text = "Reset", Flat = true, TooltipText = "Back to the preset" };
        var roll = new Button { Text = "Randomise", Flat = true, TooltipText = "Every unlocked setting with a range gets a random value inside it" };
        head.AddChild(fold);
        head.AddChild(g.Enabled);
        head.AddChild(cat);
        head.AddChild(reset);
        head.AddChild(roll);
        box.AddChild(head);

        g.Body = new VBoxContainer { Visible = false };
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        g.Body.AddChild(grid);
        box.AddChild(g.Body);
        box.AddChild(new HSeparator());
        fold.Pressed += () => { g.Body.Visible = !g.Body.Visible; fold.Text = g.Body.Visible ? "-" : "+"; };
        reset.Pressed += () => ResetPass(g);
        roll.Pressed += () => RandomisePass(g);

        foreach (JsonElement t in p.GetProperty("tunables").EnumerateArray())
        {
            var tu = new Tunable
            {
                Pass = g.Name,
                Key = t.GetProperty("key").GetString(),
                Label = t.GetProperty("label").GetString(),
                Tooltip = t.TryGetProperty("tooltip", out JsonElement tip) && tip.ValueKind == JsonValueKind.String ? tip.GetString() : null,
                Type = t.GetProperty("type").GetString(),
                Editable = t.GetProperty("editable").GetBoolean(),
                PresetValue = t.GetProperty("value").Clone(),
                Min = t.TryGetProperty("min", out JsonElement mn) ? mn.GetDouble() : null,
                Max = t.TryGetProperty("max", out JsonElement mx) ? mx.GetDouble() : null,
                Options = t.TryGetProperty("options", out JsonElement o) ? o.EnumerateArray().Select(e => e.GetString()).ToArray() : null,
            };
            string tooltip = (tu.Tooltip ?? tu.Label) + $"\n{g.Name}.{tu.Key}" + (tu.Min.HasValue ? $"  [{Num(tu.Min.Value)} .. {Num(tu.Max ?? 0)}]" : "");
            var label = new Label { Text = tu.Label, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            tu.Control = MakeControl(tu);
            tu.Control.TooltipText = tooltip;
            tu.Lock = new CheckBox { TooltipText = "Locked: Randomise leaves it alone", Text = "lock" };
            grid.AddChild(label);
            grid.AddChild(tu.Control);
            grid.AddChild(tu.Lock);
            tu.Row = label;
            g.Tunables.Add(tu);
        }

        g.Root = box;
        _passList.AddChild(box);
        return g;
    }

    private static Control MakeControl(Tunable t)
    {
        JsonElement v = t.PresetValue;
        switch (t.Type)
        {
            case "int":
            case "double":
            {
                bool isInt = t.Type == "int";
                var spin = new SpinBox
                {
                    MinValue = t.Min ?? -1_000_000,
                    MaxValue = t.Max ?? 1_000_000,
                    // A step would snap the preset's own value (0.2009 to 0.201) and send it as a change:
                    // decimals keep full precision, and only the arrows step.
                    Step = isInt ? 1 : 0,
                    CustomArrowStep = isInt ? 1 : StepFor(t),
                    Rounded = isInt,
                    AllowGreater = !t.Max.HasValue,
                    AllowLesser = !t.Min.HasValue,
                    Value = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0,
                    Editable = t.Editable,
                    CustomMinimumSize = new Vector2(120, 0),
                };
                return spin;
            }

            case "bool":
                return new CheckBox { ButtonPressed = v.ValueKind == JsonValueKind.True, Disabled = !t.Editable };
            case "enum":
            {
                var opt = new OptionButton { Disabled = !t.Editable };
                foreach (string o in t.Options ?? Array.Empty<string>())
                {
                    opt.AddItem(o);
                }

                opt.Select(Math.Max(0, Array.IndexOf(t.Options ?? Array.Empty<string>(), v.GetString())));
                return opt;
            }

            case "string":
                return new LineEdit { Text = v.GetString() ?? "", Editable = t.Editable, CustomMinimumSize = new Vector2(120, 0) };
            default:
                return new Label { Text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString(), Modulate = new Color(1, 1, 1, 0.55f), ClipText = true, CustomMinimumSize = new Vector2(120, 0) };
        }
    }

    private static double StepFor(Tunable t)
    {
        if (t.Min.HasValue && t.Max.HasValue)
        {
            double span = t.Max.Value - t.Min.Value;
            return span <= 0.1 ? 0.0005 : span <= 1.5 ? 0.01 : span <= 20 ? 0.05 : 0.5;
        }

        return 0.001;
    }

    // ---------------------------------------------------------------- values

    private static string ValueOf(Tunable t) => t.Control switch
    {
        SpinBox s when t.Type == "int" => ((long)Math.Round(s.Value)).ToString(CultureInfo.InvariantCulture),
        SpinBox s => s.Value.ToString("R", CultureInfo.InvariantCulture),
        CheckBox c => c.ButtonPressed ? "true" : "false",
        OptionButton o => o.ItemCount > 0 ? o.GetItemText(o.Selected) : "",
        LineEdit l => l.Text,
        _ => null,
    };

    private static string PresetText(Tunable t) => t.PresetValue.ValueKind switch
    {
        JsonValueKind.Number when t.Type == "int" => ((long)Math.Round(t.PresetValue.GetDouble())).ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number => t.PresetValue.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => t.PresetValue.GetString(),
        _ => null,
    };

    private static void SetValue(Tunable t, JsonElement v)
    {
        switch (t.Control)
        {
            case SpinBox s when v.ValueKind == JsonValueKind.Number:
                s.Value = v.GetDouble();
                break;
            case CheckBox c:
                c.ButtonPressed = v.ValueKind == JsonValueKind.True;
                break;
            case OptionButton o:
                o.Select(Math.Max(0, Array.IndexOf(t.Options ?? Array.Empty<string>(), v.GetString())));
                break;
            case LineEdit l:
                l.Text = v.GetString() ?? "";
                break;
        }
    }

    private void ResetPass(PassGroup g)
    {
        g.Enabled.ButtonPressed = g.PresetEnabled;
        foreach (Tunable t in g.Tunables)
        {
            SetValue(t, t.PresetValue);
        }
    }

    private void RandomisePass(PassGroup g)
    {
        foreach (Tunable t in g.Tunables)
        {
            if (t.Lock.ButtonPressed || !t.Editable || t.Control is not SpinBox s || !t.Min.HasValue || !t.Max.HasValue)
            {
                continue;
            }

            double v = t.Min.Value + _random.NextDouble() * (t.Max.Value - t.Min.Value);
            s.Value = t.Type == "int" ? Math.Round(v) : v;
        }
    }

    private Tunable Find(string pass, string key) =>
        _passes.FirstOrDefault(p => p.Name == pass)?.Tunables.FirstOrDefault(t => t.Key == key);

    private void ResetKnobs()
    {
        _knobPresetValues.Clear();
        foreach ((string _, string _, (string Pass, string Key, double Sign)[] targets) in Knobs)
        {
            foreach ((string pass, string key, double _) in targets)
            {
                Tunable t = Find(pass, key);
                if (t?.Control is SpinBox s)
                {
                    _knobPresetValues[pass + "." + key] = s.Value;
                }
            }
        }

        foreach (HSlider s in _knobSliders)
        {
            s.SetValueNoSignal(0);
        }
    }

    /// <summary>Moves a knob's tunables from their preset value toward the end of their range: half way at full travel.</summary>
    private void ApplyKnob(int knob, double k)
    {
        foreach ((string pass, string key, double sign) in Knobs[knob].Targets)
        {
            Tunable t = Find(pass, key);
            if (t?.Control is not SpinBox s || t.Lock.ButtonPressed || !_knobPresetValues.TryGetValue(pass + "." + key, out double baseValue))
            {
                continue;
            }

            double dir = k * sign;
            double lo = t.Min ?? baseValue - Math.Abs(baseValue), hi = t.Max ?? baseValue + Math.Abs(baseValue) + 1;
            double v = dir >= 0 ? baseValue + dir * (hi - baseValue) * 0.5 : baseValue + dir * (baseValue - lo) * 0.5;
            s.Value = t.Type == "int" ? Math.Round(v) : v;
        }
    }

    /// <summary>The run's arguments: the preset, then only what differs from it.</summary>
    public List<string> BuildRunArgs(string outDir)
    {
        var args = new List<string> { "run", "--out", outDir, "--preset", CurrentPreset(), "--seed", ((long)_seed.Value).ToString(CultureInfo.InvariantCulture) };
        string[] size = (_size.ItemCount > 0 ? _size.GetItemText(_size.Selected) : "1024 x 1024").Split(" x ");
        args.AddRange(new[] { "--width", size[0], "--height", size[1] });
        if (_fast.ButtonPressed)
        {
            args.Add("--fast");
        }

        if (_stepPreviews.ButtonPressed)
        {
            args.Add("--step-previews");
        }

        foreach (PassGroup g in _passes)
        {
            if (g.Enabled.ButtonPressed != g.PresetEnabled)
            {
                args.Add(g.Enabled.ButtonPressed ? "--enable" : "--disable");
                args.Add(g.Name);
            }

            foreach (Tunable t in g.Tunables)
            {
                if (!t.Editable)
                {
                    continue;
                }

                string now = ValueOf(t), was = PresetText(t);
                if (now != null && now != was && !SameNumber(t, now, was))
                {
                    args.Add("--set");
                    args.Add($"{g.Name}.{t.Key}={now}");
                }
            }
        }

        return args;
    }

    private static bool SameNumber(Tunable t, string a, string b) =>
        (t.Type == "double" || t.Type == "int")
        && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
        && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
        && Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));

    private string CurrentPreset() => _presetValues.Count > 0 ? _presetValues[_preset.Selected] : "felucca-stage18";

    // ---------------------------------------------------------------- generate / export

    private async Task GenerateAsync()
    {
        if (_busy)
        {
            return;
        }

        string outDir = MapGenCli.NewRunDir(CurrentPreset(), (long)_seed.Value);
        List<string> args = BuildRunArgs(outDir);
        _steps.Clear();
        _lastWorld = null;
        _openWorld.Disabled = true;
        _stepSlider.Editable = false;
        _log.Clear();
        Log($"[b]Generate[/b] {string.Join(' ', args.Skip(3).Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        _cancelSource = new CancellationTokenSource();
        SetBusy(true, "Generating ...");
        JsonElement? done = null;
        string error = null;
        int code = await MapGenCli.RunAsync(args, j => Callable.From(() =>
        {
            string ev = j.TryGetProperty("event", out JsonElement e) ? e.GetString() : "";
            switch (ev)
            {
                case "start":
                    _mapW = j.GetProperty("width").GetInt32();
                    _mapH = j.GetProperty("height").GetInt32();
                    break;
                case "pass":
                    OnPassEvent(j, outDir);
                    break;
                case "done":
                    done = j;
                    break;
                case "error":
                    error = j.GetProperty("message").GetString();
                    break;
            }
        }).CallDeferred(), line => Callable.From(() => Log($"[color=gray]{Escape(line)}[/color]")).CallDeferred(), _cancelSource.Token);

        Callable.From(() =>
        {
            SetBusy(false, code == -1 ? "Cancelled" : error != null ? $"Failed: {error}" : code != 0 ? $"Failed (exit {code})" : "Done");
            if (code == 0 && done is JsonElement d)
            {
                _lastRun = outDir;
                _export.Disabled = false;
                _savePreset.Disabled = false;
                ShowFinal();
                ShowStats(d.GetProperty("stats"));
                Log($"[b]Done[/b] in {d.GetProperty("elapsed_ms").GetInt64()} ms, hash {d.GetProperty("hash").GetString()[..12]}. Folder: {Escape(outDir)}");
            }
        }).CallDeferred();
    }

    private void OnPassEvent(JsonElement j, string outDir)
    {
        int index = j.GetProperty("index").GetInt32(), count = j.GetProperty("count").GetInt32();
        string name = j.GetProperty("name").GetString();
        bool enabled = j.GetProperty("enabled").GetBoolean();
        _status.Text = $"Pass {index + 1}/{count}: {name}";
        if (!enabled)
        {
            return;
        }

        string warn = j.TryGetProperty("warnings", out JsonElement w) && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0
            ? $"  [color=orange]{Escape(w[0].GetString())}[/color]" : "";
        Log($"{name}  {j.GetProperty("ms").GetInt64()} ms{warn}");
        if (j.TryGetProperty("preview", out JsonElement p) && p.ValueKind == JsonValueKind.String)
        {
            _steps.Add((index, name, Path.Combine(outDir, p.GetString())));
            _stepSlider.MaxValue = _steps.Count - 1;
            _stepSlider.Editable = true;
            _stepSlider.SetValueNoSignal(_steps.Count - 1);
            ShowStep(_steps.Count - 1);
        }
    }

    private async Task ExportAsync()
    {
        if (_busy || _lastRun == null)
        {
            return;
        }

        string world = MapGenCli.NewWorldProjectDir(_lastRun);
        var args = new List<string> { "export", "--run", _lastRun, "--world-project", world };
        _cancelSource = new CancellationTokenSource();
        SetBusy(true, "Exporting ...");
        Log("[b]Export[/b] regenerating to check the hash ...");
        JsonElement? done = null;
        string error = null;
        int code = await MapGenCli.RunAsync(args, j => Callable.From(() =>
        {
            string ev = j.TryGetProperty("event", out JsonElement e) ? e.GetString() : "";
            if (ev == "done")
            {
                done = j;
            }
            else if (ev == "error")
            {
                error = j.GetProperty("message").GetString();
            }
            else if (ev == "pass")
            {
                _status.Text = $"Export: pass {j.GetProperty("index").GetInt32() + 1}/{j.GetProperty("count").GetInt32()}";
            }
        }).CallDeferred(), line => Callable.From(() => Log($"[color=gray]{Escape(line)}[/color]")).CallDeferred(), _cancelSource.Token);

        Callable.From(() =>
        {
            bool ok = code == 0 && done is JsonElement d && d.GetProperty("ok").GetBoolean();
            SetBusy(false, ok ? "Exported and verified" : error != null ? $"Export failed: {error}" : $"Export failed (exit {code})");
            if (ok)
            {
                JsonElement v = done.Value.GetProperty("verify");
                Log($"[b]Exported[/b] {v.GetProperty("land_cells_checked").GetInt32()} cells and {v.GetProperty("statics_found").GetInt32()} statics read back, 0 mismatches. "
                    + $"Map files: {Escape(Path.Combine(_lastRun, "export", "map"))}. World project: {Escape(world)}");
                _lastWorld = world;
                JsonElement origin = done.Value.GetProperty("world").GetProperty("origin");
                _originX = origin[0].GetInt32();
                _originY = origin[1].GetInt32();
                _openWorld.Disabled = OpenInWorld == null;
            }
        }).CallDeferred();
    }

    private void OpenWorld()
    {
        if (_lastWorld != null && OpenInWorld != null)
        {
            OpenInWorld(_lastWorld, 0, _originX + _mapW / 2, _originY + _mapH / 2);
        }
    }

    private void SavePreset()
    {
        string name = MapGenCli.Slug(_savePresetName.Text);
        if (_lastRun == null || name.Length == 0)
        {
            _status.Text = "Name the preset first";
            return;
        }

        Directory.CreateDirectory(MapGenCli.UserPresetsDir);
        string path = Path.Combine(MapGenCli.UserPresetsDir, name + ".preset.json");
        File.Copy(Path.Combine(_lastRun, "preset.json"), path, overwrite: true);
        _status.Text = $"Saved preset '{name}'";
        _ = LoadSchemaAsync(path);
    }

    // ---------------------------------------------------------------- preview

    private void ShowFinal()
    {
        if (_lastRun == null)
        {
            return;
        }

        string file = _layer.Selected switch { 1 => "biome.png", 2 => "height.png", _ => "radar.png" };
        ShowImage(Path.Combine(_lastRun, file));
        _stepLabel.Text = "final";
    }

    private void ShowStep(int i)
    {
        if (i < 0 || i >= _steps.Count)
        {
            return;
        }

        ShowImage(_steps[i].File);
        _stepLabel.Text = $"{_steps[i].Index + 1}. {_steps[i].Name}";
    }

    private void ShowImage(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Image img = Image.LoadFromFile(path);
        if (img != null)
        {
            _preview.Texture = ImageTexture.CreateFromImage(img);
        }
    }

    private void OnPreviewInput(InputEvent e)
    {
        if (_preview.Texture == null || _mapW == 0 || e is not InputEventMouse m)
        {
            return;
        }

        // The texture is drawn centred and scaled to fit (KeepAspectCentered).
        Vector2 tex = _preview.Texture.GetSize(), box = _preview.Size;
        float scale = Math.Min(box.X / tex.X, box.Y / tex.Y);
        Vector2 offset = (box - tex * scale) / 2;
        Vector2 px = (m.Position - offset) / scale;
        if (px.X < 0 || px.Y < 0 || px.X >= tex.X || px.Y >= tex.Y)
        {
            _cellLabel.Text = "";
            return;
        }

        int x = (int)(px.X * _mapW / tex.X), y = (int)(px.Y * _mapH / tex.Y);
        _cellLabel.Text = $"cell {x}, {y}";
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && _lastWorld != null && OpenInWorld != null)
        {
            OpenInWorld(_lastWorld, 0, _originX + x, _originY + y);
        }
    }

    private void ShowStats(JsonElement stats)
    {
        foreach (Node c in _likeness.GetChildren())
        {
            _likeness.RemoveChild(c);
            c.QueueFree();
        }

        if (!stats.TryGetProperty("available", out JsonElement a) || !a.GetBoolean())
        {
            _score.Text = "No land ids yet: nothing to measure.";
            return;
        }

        _score.Text = $"Score {stats.GetProperty("score").GetDouble():0.0} / 100 (a guide; the analyzer is the judge)";
        _likeness.AddChild(new Label { Text = "" });
        _likeness.AddChild(new Label { Text = "this map" });
        _likeness.AddChild(new Label { Text = "Felucca" });
        JsonElement fel = stats.GetProperty("felucca");
        foreach (JsonProperty m in stats.GetProperty("measured").EnumerateObject())
        {
            bool share = m.Name != "statics_per_100_land";
            _likeness.AddChild(new Label { Text = m.Name.Replace('_', ' ') });
            _likeness.AddChild(new Label { Text = share ? $"{m.Value.GetDouble() * 100:0.0}%" : $"{m.Value.GetDouble():0.0}" });
            _likeness.AddChild(new Label
            {
                Text = fel.TryGetProperty(m.Name, out JsonElement f) ? (share ? $"{f.GetDouble() * 100:0.0}%" : $"{f.GetDouble():0.0}") : "-",
                Modulate = new Color(1, 1, 1, 0.6f),
            });
        }
    }

    // ---------------------------------------------------------------- helpers

    private void ApplyFilter(string text)
    {
        string q = (text ?? "").Trim().ToLowerInvariant();
        foreach (PassGroup g in _passes)
        {
            bool passHit = q.Length == 0 || g.Name.ToLowerInvariant().Contains(q) || g.Category.ToLowerInvariant().Contains(q);
            bool any = passHit;
            foreach (Tunable t in g.Tunables)
            {
                bool hit = passHit || t.Label.ToLowerInvariant().Contains(q) || t.Key.ToLowerInvariant().Contains(q);
                t.Row.Visible = hit;
                t.Control.Visible = hit;
                t.Lock.Visible = hit;
                any |= hit;
            }

            g.Root.Visible = any;
            if (q.Length > 0 && any && !passHit)
            {
                g.Body.Visible = true;
            }
        }
    }

    private void SetAllOpen(bool open)
    {
        foreach (PassGroup g in _passes)
        {
            g.Body.Visible = open;
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _generate.Disabled = busy;
        _cancel.Disabled = !busy;
        _preset.Disabled = busy;
        if (busy)
        {
            _export.Disabled = true;
        }

        _status.Text = status;
    }

    private void Log(string bbcode) => _log.AppendText(bbcode + "\n");

    private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static Label Heading(string text) => new() { Text = text, ThemeTypeVariation = "HeaderSmall" };

    /// <summary>Stops a run in progress (TearDown, before an assembly reload).</summary>
    public void Shutdown() => _cancelSource?.Cancel();
}
#endif
