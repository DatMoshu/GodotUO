#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Map Generator main-screen tab (ADR-0030). Setup and schema-driven pass settings share
/// a column beside the result, with generation and export actions available in either tab.
/// </summary>
[Tool]
public partial class MapGenView : VBoxContainer
{
    public const string TabName = "MapGen";

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
        public Button Fold;
        public readonly List<Tunable> Tunables = new();
    }

    private readonly List<PassGroup> _passes = new();
    private readonly Dictionary<string, double> _knobPresetValues = new();
    private readonly List<HSlider> _knobSliders = new();
    private readonly List<(int Index, string Name, string File)> _steps = new();
    private readonly Random _random = new();

    private OptionButton _preset, _size, _layer, _table;
    private readonly List<string> _presetValues = new();
    private readonly List<string> _tableValues = new();
    private string _tableFile;
    private string _selectedTable = "guo";
    private LineEdit _seed;
    private CheckBox _fast, _stepPreviews;
    private Button _generate, _cancel, _export, _openWorld, _savePreset, _restoreRun, _reloadSchema, _final;
    private LineEdit _savePresetName, _filter;
    private VBoxContainer _passList;
    private TextureRect _preview;
    private HSlider _stepSlider;
    private Label _stepLabel, _status, _score, _cellLabel, _resultLabel, _presetDescription, _empty, _warningSummary;
    private GridContainer _likeness;
    private RichTextLabel _log;
    private ProgressBar _progress;
    private int _warningCount;
    private bool _showingStep, _radarColours;

    private bool _busy;
    private string _lastRun, _lastWorld, _resultText, _resultHash;
    private CompletedRun _previous;
    private sealed record CompletedRun(string Run, string World, string Label, string Hash, int Width, int Height,
        int Facet, int X, int Y, JsonElement Stats, bool RadarColours, int Warnings, string Log,
        (int Index, string Name, string File)[] Steps);
    private JsonElement _resultStats;
    private int _mapW, _mapH, _facet, _originX, _originY;
    private JsonElement? _schema;
    private volatile bool _retired;

    /// <summary>The schema the tab is showing (for the smoke check).</summary>
    public int PassGroupCount => _passes.Count;
    public int TunableControlCount => _passes.Sum(p => p.Tunables.Count);
    public string LastRun => _lastRun;

    /// <summary>The transition table the next run uses: guo, guo-core, dragon or a file path.</summary>
    public string CurrentTable => _selectedTable;

    /// <summary>The tables the picker offers (ids, and file paths once picked).</summary>
    public IReadOnlyList<string> TableChoices => _tableValues;

    public MapGenView()
    {
        Name = "MapGenView";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Visible = false;

        AddChild(new Label { Text = "UO Map Generator", ThemeTypeVariation = "HeaderLarge" });

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        var settings = new TabContainer();
        var setup = BuildLeft();
        setup.Name = "Setup";
        settings.AddChild(setup);
        var passes = BuildMiddle();
        passes.Name = "Passes";
        settings.AddChild(passes);
        split.AddChild(Pane(settings, 295, SizeFlags.Fill));
        split.AddChild(Pane(BuildRight(), 240, SizeFlags.ExpandFill));
        AddChild(BuildActions());
        SetBusy(false, "Choose a preset to begin.");
    }

    // Keep a tab or log's content minimum from widening the editor's entire main screen.
    private static Control Pane(Control content, int width, SizeFlags flags)
    {
        var pane = new Control { ClipContents = true, CustomMinimumSize = new Vector2(width, 0), SizeFlagsHorizontal = flags };
        pane.AddChild(content);
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return pane;
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
        var scroll = new ScrollContainer { FollowFocus = true };
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(col);

        col.AddChild(Heading("Preset"));
        _preset = new OptionButton { FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _preset.ItemSelected += i => _ = LoadSchemaAsync(_presetValues[(int)i]);
        col.AddChild(_preset);
        _presetDescription = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2 };
        col.AddChild(_presetDescription);

        col.AddChild(Heading("Seed"));
        var seedRow = new HBoxContainer();
        _seed = new LineEdit { Text = "1234567", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Signed 64-bit integer. Reuse the seed and settings to reproduce a run." };
        var dice = new Button { Text = "Roll", TooltipText = "A new random seed" };
        dice.Pressed += () => _seed.Text = _random.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        seedRow.AddChild(_seed);
        seedRow.AddChild(dice);
        col.AddChild(seedRow);

        col.AddChild(Heading("Size (tiles)"));
        _size = new OptionButton { TooltipText = "Start with 256 × 256 to tune quickly. Larger facets need more time and storage." };
        col.AddChild(_size);

        col.AddChild(Heading("Transition table"));
        _table = new OptionButton { FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = "Which tiles blend one ground into another. GUO core uses the same table on every machine." };
        _table.ItemSelected += i =>
        {
            if (_tableValues[(int)i] == FileChoice)
            {
                PickTableFile();
            }
            else
                _selectedTable = _tableValues[(int)i];
        };
        col.AddChild(_table);

        col.AddChild(Heading("Run options"));
        _fast = new CheckBox { Text = "Fast draft", TooltipText = "Skips heavy passes, including roads, stamps and scatter. Turn it off for the full preset." };
        _stepPreviews = new CheckBox { Text = "Save pass previews", ButtonPressed = true, TooltipText = "Save a picture after each enabled pass. This adds time and disk use." };
        col.AddChild(_fast);
        col.AddChild(_stepPreviews);

        col.AddChild(Heading("Shape terrain"));
        for (int k = 0; k < Knobs.Length; k++)
        {
            int knob = k;
            col.AddChild(new Label { Text = Knobs[k].Label, TooltipText = Knobs[k].Tip, MouseFilter = MouseFilterEnum.Pass });
            var slider = new HSlider { MinValue = -1, MaxValue = 1, Step = 0.05, Value = 0, TooltipText = Knobs[k].Tip };
            slider.ValueChanged += v => ApplyKnob(knob, v);
            _knobSliders.Add(slider);
            col.AddChild(slider);
        }

        col.AddChild(Heading("Save completed settings"));
        var save = new HBoxContainer();
        _savePresetName = new LineEdit { PlaceholderText = "Preset name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _savePreset = new Button { Text = "Save", Disabled = true, TooltipText = "Save the completed run's passes and seed as a preset." };
        _savePreset.Pressed += SavePreset;
        save.AddChild(_savePresetName);
        save.AddChild(_savePreset);
        col.AddChild(save);
        return scroll;
    }

    private Control BuildActions()
    {
        var actions = new VBoxContainer();
        actions.AddChild(new HSeparator());
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2 };
        actions.AddChild(_status);
        _progress = new ProgressBar { ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 5) };
        actions.AddChild(_progress);
        var buttons = new HFlowContainer();
        actions.AddChild(buttons);
        _generate = new Button { Text = "Generate", TooltipText = "Generate from the settings in Setup and Passes." };
        _generate.Pressed += () => _ = GenerateAsync();
        _cancel = new Button { Text = "Cancel", Disabled = true };
        _cancel.Pressed += () => _cancelSource?.Cancel();
        _export = new Button { Text = "Export map files", Disabled = true, TooltipText = "Regenerate the last run, check it matches, write map0/staidx0/statics0.mul into the run's export folder and read every cell back. Also writes a world project for UO World." };
        _export.Pressed += () => _ = ExportAsync();
        _openWorld = new Button { Text = "Open in UO World", Disabled = true, TooltipText = "Open the exported map's world project in the UO World tab. Deploying to a shard is a separate step there." };
        _openWorld.Pressed += OpenWorld;
        _restoreRun = new Button { Text = "Show previous completed run", Disabled = true,
            TooltipText = "Restore the last successful preview and its export actions after an incomplete attempt." };
        _restoreRun.Pressed += RestoreCompletedRun;
        _reloadSchema = new Button { Text = "Retry loading generator", Visible = false };
        _reloadSchema.Pressed += () => _ = LoadSchemaAsync(null);
        foreach (Button b in new[] { _generate, _cancel, _restoreRun, _export, _openWorld, _reloadSchema })
        {
            buttons.AddChild(b);
        }

        return actions;
    }

    private CancellationTokenSource _cancelSource;

    // ---------------------------------------------------------------- middle column

    private Control BuildMiddle()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filter = new LineEdit { PlaceholderText = "Filter passes and settings", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _filter.TextChanged += ApplyFilter;
        col.AddChild(_filter);
        var bar = new HFlowContainer();
        var expand = new Button { Text = "Open all" };
        expand.Pressed += () => SetAllOpen(true);
        var collapse = new Button { Text = "Close all" };
        collapse.Pressed += () => SetAllOpen(false);
        var resetAll = new Button { Text = "Reset all", TooltipText = "Every pass back to the preset" };
        resetAll.Pressed += () => { foreach (PassGroup g in _passes) ResetPass(g); ResetKnobs(); };
        bar.AddChild(expand);
        bar.AddChild(collapse);
        bar.AddChild(resetAll);
        col.AddChild(bar);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, FollowFocus = true };
        _passList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_passList);
        col.AddChild(scroll);
        return col;
    }

    // ---------------------------------------------------------------- right column

    private Control BuildRight()
    {
        var col = new VBoxContainer();
        _resultLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "No completed run", ThemeTypeVariation = "HeaderSmall" };
        col.AddChild(_resultLabel);
        var bar = new HBoxContainer();
        _layer = new OptionButton { FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Colour layer for the final map." };
        _layer.AddItem("Radar");
        _layer.AddItem("Biomes");
        _layer.AddItem("Height");
        _layer.ItemSelected += _ => ShowFinal();
        bar.AddChild(_layer);
        _cellLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
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
        _preview.MouseExited += () => _cellLabel.Text = "";
        _empty = new Label { Text = "Choose a preset and select Generate.", HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, SizeFlagsVertical = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_empty);
        _preview.Hide();
        col.AddChild(_preview);

        var stepRow = new HBoxContainer();
        stepRow.AddChild(new Label { Text = "Pass" });
        _stepSlider = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, MinValue = 0, MaxValue = 0, Step = 1, Editable = false };
        _stepSlider.ValueChanged += v =>
        {
            if (v == _steps.Count) ShowFinal();
            else ShowStep((int)v);
        };
        stepRow.AddChild(_stepSlider);
        _final = new Button { Text = "Final map", Disabled = true };
        _final.Pressed += ShowFinal;
        stepRow.AddChild(_final);
        col.AddChild(stepRow);
        _stepLabel = new Label { Text = "Pass previews appear here when enabled.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_stepLabel);

        var details = new Button { Text = "Run details", ToggleMode = true, Alignment = HorizontalAlignment.Left };
        _warningSummary = new Label();
        var detailRow = new HBoxContainer();
        detailRow.AddChild(details);
        detailRow.AddChild(_warningSummary);
        col.AddChild(detailRow);
        var diagnostics = new TabContainer { Visible = false, CustomMinimumSize = new Vector2(0, 112) };
        details.Toggled += visible => diagnostics.Visible = visible;
        col.AddChild(diagnostics);
        var metrics = new ScrollContainer { Name = "Felucca likeness" };
        var metricList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        metrics.AddChild(metricList);
        _score = new Label { Text = "Generate a map to measure it.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        metricList.AddChild(_score);
        _likeness = new GridContainer { Columns = 3 };
        metricList.AddChild(_likeness);

        _log = new RichTextLabel { Name = "Log", BbcodeEnabled = true, ScrollFollowing = true, SelectionEnabled = true };
        diagnostics.AddChild(_log);
        diagnostics.AddChild(metrics);
        return col;
    }

    // ---------------------------------------------------------------- schema → controls

    private async Task LoadSchemaAsync(string preset)
    {
        if (_retired || _busy) return;
        var clock = Stopwatch.StartNew();
        SetBusy(true, preset == null ? "Reading the generator…" : $"Loading {Path.GetFileName(preset)}…");
        var args = new List<string> { "schema" };
        if (preset != null)
        {
            args.Add("--preset");
            args.Add(preset);
        }

        JsonElement? schema = null;
        string error;
        try { (schema, error) = await MapGenCli.QueryAsync(args); }
        catch (Exception ex) { error = ex.Message; }
        OnUi(() =>
        {
            if (schema is JsonElement s)
            {
                long queryMs = clock.ElapsedMilliseconds;
                LoadSchema(s, preset);
                Log($"Preset query {queryMs} ms · building controls {clock.ElapsedMilliseconds - queryMs} ms");
            }
            else if (_schema.HasValue)
                _preset.Select(_loadedPreset);
            if (error.Length > 0)
                Log(Escape(error));
            SetBusy(false, schema.HasValue ? "Adjust the setup, then generate." : "Could not load the generator preset. " + error);
        });
    }

    private int _loadedPreset;

    /// <summary>Builds the controls from a <c>schema</c> object (docs/data_formats.md §26).</summary>
    public void LoadSchema(JsonElement schema, string presetPath = null)
    {
        _schema = schema;
        string presetId = schema.GetProperty("preset").GetProperty("id").GetString();
        JsonElement preset = schema.GetProperty("preset");
        _seed.Text = preset.TryGetProperty("seed", out JsonElement seed) && seed.ValueKind == JsonValueKind.Number
            ? seed.GetInt64().ToString(CultureInfo.InvariantCulture) : "1234567";
        _presetDescription.Text = preset.GetProperty("description").GetString() ?? "";
        _presetDescription.TooltipText = _presetDescription.Text;
        _presetDescription.MouseFilter = MouseFilterEnum.Pass;

        FillPresets(schema, presetPath ?? presetId);
        FillSizes(schema);
        FillTables(schema);

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
        if (schema.TryGetProperty("warnings", out JsonElement warnings))
            foreach (JsonElement warning in warnings.EnumerateArray())
                Log(Escape(warning.GetString()));
        Log($"{_passes.Count} passes, {TunableControlCount} settings, preset {presetId}");
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

        _loadedPreset = Math.Max(0, _presetValues.IndexOf(current));
        _preset.Select(_loadedPreset);
    }

    private const string FileChoice = "<file>";

    /// <summary>The schema's tables, then a table file once one was picked, then "Other file...".</summary>
    private void FillTables(JsonElement schema)
    {
        string old = _tableValues.Count > 0 ? CurrentTable : "guo";
        _tableValues.Clear();
        _table.Clear();
        if (schema.TryGetProperty("tables", out JsonElement tables))
        {
            foreach (JsonElement t in tables.EnumerateArray())
            {
                _tableValues.Add(t.GetProperty("id").GetString());
                _table.AddItem(t.GetProperty("label").GetString());
                int i = _table.ItemCount - 1;
                _table.SetItemTooltip(i, t.GetProperty("description").GetString());
                _table.SetItemDisabled(i, !t.GetProperty("available").GetBoolean());
            }
        }
        else
        {
            _tableValues.Add("guo");
            _table.AddItem("GUO");
        }

        if (_tableFile != null)
        {
            _tableValues.Add(_tableFile);
            _table.AddItem("file: " + Path.GetFileName(_tableFile));
            _table.SetItemTooltip(_table.ItemCount - 1, _tableFile);
        }

        _tableValues.Add(FileChoice);
        _table.AddItem("Other file...");
        _table.SetItemTooltip(_table.ItemCount - 1, "A transition table JSON of your own: GUO's format (guo.mapgen.transitions/1) or a LandBrush (Dragon) export.");
        SelectTable(old);
    }

    /// <summary>Selects a table by id or file path; false (and GUO selected) when it is not offered or not prepared.</summary>
    public bool SelectTable(string value)
    {
        int sel = _tableValues.IndexOf(value);
        bool ok = sel >= 0 && !_table.IsItemDisabled(sel) && value != FileChoice;
        _table.Select(ok ? sel : 0);
        _selectedTable = ok ? value : "guo";
        return ok;
    }

    private void PickTableFile()
    {
        string back = _selectedTable;
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Choose a transition table",
            CurrentDir = _tableFile != null ? Path.GetDirectoryName(_tableFile) : EditorData.RepoRoot,
        };
        dialog.AddFilter("*.json", "Transition table");
        dialog.FileSelected += path =>
        {
            _tableFile = path;
            if (_schema is JsonElement s)
            {
                FillTables(s);
            }

            SelectTable(path);
            Log($"Transition table: {Escape(Path.GetFileName(path))}");
            dialog.QueueFree();
        };
        dialog.Canceled += () =>
        {
            SelectTable(back);
            dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCenteredRatio(0.8f);
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
        int order = p.GetProperty("index").GetInt32() + 1;
        string dependency = p.TryGetProperty("reads", out var reads) && p.TryGetProperty("writes", out var writes)
            ? $"\nReads {reads.GetString()}\nWrites {writes.GetString()}" : "";
        string heavy = p.GetProperty("heavy").GetBoolean() ? "\nSkipped in Fast draft unless explicitly enabled." : "";
        g.Enabled = new CheckBox { ButtonPressed = g.PresetEnabled, TooltipText = $"Enable {g.Name}.{heavy}{dependency}" };
        g.Fold = new Button { Text = $"{order:00}  {g.Name}", Alignment = HorizontalAlignment.Left, ToggleMode = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, Flat = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = $"{g.Category} · {g.Name}{heavy}{dependency}" };
        var reset = new Button { Text = "Reset", Flat = true, TooltipText = "Back to the preset" };
        var roll = new Button { Text = "Randomise", Flat = true, TooltipText = "Every unlocked setting with a range gets a random value inside it" };
        head.AddChild(g.Enabled);
        head.AddChild(g.Fold);
        box.AddChild(head);

        g.Body = new VBoxContainer { Visible = false };
        var tools = new HFlowContainer();
        tools.AddChild(new Label { Text = g.Category });
        tools.AddChild(reset);
        tools.AddChild(roll);
        g.Body.AddChild(tools);
        box.AddChild(g.Body);
        box.AddChild(new HSeparator());
        g.Fold.Toggled += open => g.Body.Visible = open;
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
            string tooltip = (tu.Tooltip ?? tu.Label) + $"\n{g.Name}.{tu.Key}"
                + (tu.Min.HasValue ? $"  [{Num(tu.Min.Value)} .. {Num(tu.Max ?? 0)}]" : "")
                + $"\nPreset value: {tu.PresetValue}";
            var label = new Label { Text = tu.Label, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            tu.Control = MakeControl(tu);
            tu.Control.TooltipText = tooltip;
            tu.Lock = new CheckBox { TooltipText = "Lock this setting against Randomise and the terrain sliders.", Text = "Lock" };
            var row = new VBoxContainer();
            var input = new HBoxContainer();
            tu.Control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            input.AddChild(tu.Control);
            input.AddChild(tu.Lock);
            row.AddChild(label);
            row.AddChild(input);
            g.Body.AddChild(row);
            tu.Row = row;
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
                var opt = new OptionButton { Disabled = !t.Editable, FitToLongestItem = false,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new Vector2(100, 0) };
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
        var args = new List<string> { "run", "--out", outDir, "--preset", CurrentPreset(), "--seed", _seed.Text.Trim() };
        string[] size = (_size.ItemCount > 0 ? _size.GetItemText(_size.Selected) : "1024 x 1024").Split(" x ");
        args.AddRange(new[] { "--width", size[0], "--height", size[1] });
        if (CurrentTable != "guo" && CurrentTable != FileChoice)
        {
            args.AddRange(new[] { "--brushes", CurrentTable });
        }

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
        if (_busy || _retired || !_schema.HasValue) return;
        if (!long.TryParse(_seed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed))
        {
            _status.Text = "Enter a whole-number seed in the signed 64-bit range.";
            _seed.GrabFocus();
            return;
        }

        string preset = CurrentPreset();
        string outDir = MapGenCli.NewRunDir(preset, seed);
        List<string> args = BuildRunArgs(outDir);
        bool fast = args.Contains("--fast");
        string setup = $"{_preset.GetItemText(_preset.Selected)} · seed {seed} · {_size.GetItemText(_size.Selected)}";
        if (_lastRun != null)
            _previous = new CompletedRun(_lastRun, _lastWorld, _resultText, _resultHash, _mapW, _mapH, _facet,
                _originX, _originY, _resultStats, _radarColours, _warningCount, _log.GetParsedText(), _steps.ToArray());
        _steps.Clear();
        _resultLabel.Text = $"Generating {(fast ? "fast draft" : "map")}\n{setup}";
        _resultLabel.TooltipText = "";
        _lastRun = _lastWorld = null;
        _mapW = _mapH = _originX = _originY = 0;
        ClearPreview("Waiting for the first preview…");
        _stepLabel.Text = "Pass previews are " + (_stepPreviews.ButtonPressed ? "enabled." : "off for this run.");
        _cellLabel.Text = "";
        _score.Text = "Waiting for a completed run.";
        ClearStats();
        _savePreset.Disabled = true;
        _openWorld.Disabled = true;
        _stepSlider.Editable = false;
        _showingStep = false;
        _stepSlider.MaxValue = 0;
        _stepSlider.SetValueNoSignal(0);
        _warningCount = 0;
        UpdateWarnings();
        _log.Clear();
        Log($"[b]Generate[/b] {Escape(string.Join(' ', args.Skip(3)))}");
        SetBusy(true, "Starting MapGen…");
        var (code, done, error) = await RunCommandAsync(args, j =>
        {
            switch (EventName(j))
            {
                case "start":
                    _mapW = j.GetProperty("width").GetInt32();
                    _mapH = j.GetProperty("height").GetInt32();
                    break;
                case "pass":
                    OnPassEvent(j, outDir);
                    break;
                case "pass_started":
                    ShowProgress(j);
                    break;
                case "phase":
                    _status.Text = j.GetProperty("name").GetString();
                    break;
            }
        });

        OnUi(() =>
        {
            bool completed = code == 0 && done.HasValue;
            if (completed)
            {
                JsonElement d = done.Value;
                string hash = d.GetProperty("hash").GetString();
                _lastRun = outDir;
                _previous = null;
                _resultStats = d.GetProperty("stats");
                _resultHash = hash;
                _radarColours = d.TryGetProperty("radar_colours", out var radar) && radar.GetBoolean();
                _resultText = $"{(fast ? "Fast draft complete" : "Completed run")} · {hash[..12]}\n{setup}";
                _resultLabel.Text = _resultText;
                _resultLabel.TooltipText = $"{hash}\n{outDir}";
                LogWarnings(d);
                ShowFinal();
                ShowStats(_resultStats);
                string previewMs = d.TryGetProperty("step_preview_ms", out var ms) ? $", pass images {ms.GetInt64()} ms" : "";
                Log($"[b]Done[/b] in {d.GetProperty("elapsed_ms").GetInt64()} ms{previewMs}. Folder: {Escape(outDir)}");
            }
            else
            {
                _resultLabel.Text = $"{(code == -1 ? "Cancelled attempt" : "Failed attempt")}\n{setup}";
                _resultLabel.TooltipText = "";
                if (_preview.Texture == null)
                    ClearPreview(code == -1 ? "Generation was cancelled." : "No preview was produced. Check the message below and retry.");
            }

            SetBusy(false, completed ? "Inspect the final map or step through the passes." : code == -1 ? "Generation cancelled." : error ?? $"No completed result (exit {code}). See Run details.");
        });
    }

    private void RestoreCompletedRun()
    {
        if (_busy || _lastRun != null || _previous is not { } previous) return;
        _previous = null;
        (_lastRun, _lastWorld, _resultText, _resultHash, _mapW, _mapH, _facet, _originX, _originY, _resultStats, _radarColours) =
            (previous.Run, previous.World, previous.Label, previous.Hash, previous.Width, previous.Height,
                previous.Facet, previous.X, previous.Y, previous.Stats, previous.RadarColours);
        _log.Clear();
        Log(Escape(previous.Log));
        _warningCount = previous.Warnings;
        UpdateWarnings();
        _steps.Clear();
        _steps.AddRange(previous.Steps);
        _cellLabel.Text = "";
        _resultLabel.Text = _lastWorld == null ? _resultText : "Verified export\n" + _resultText;
        _resultLabel.TooltipText = $"{_resultHash}\n{_lastRun}";
        ShowFinal();
        ShowStats(_resultStats);
        SetBusy(false, "Showing the previous completed run");
    }

    private void OnPassEvent(JsonElement j, string outDir)
    {
        int index = j.GetProperty("index").GetInt32();
        string name = j.GetProperty("name").GetString();
        bool enabled = j.GetProperty("enabled").GetBoolean();
        if (!enabled)
        {
            return;
        }

        Log($"{name} · {j.GetProperty("ms").GetInt64()} ms");
        LogWarnings(j);
        if (j.TryGetProperty("preview", out JsonElement p) && p.ValueKind == JsonValueKind.String)
        {
            bool follow = _steps.Count == 0 || _stepSlider.Value == _steps.Count - 1;
            _steps.Add((index, name, Path.Combine(outDir, p.GetString())));
            _stepSlider.MaxValue = _steps.Count - 1;
            _stepSlider.Editable = true;
            if (follow)
            {
                _stepSlider.SetValueNoSignal(_steps.Count - 1);
                ShowStep(_steps.Count - 1);
            }
        }
    }

    private async Task ExportAsync()
    {
        if (_busy || _lastRun == null || _lastWorld != null)
        {
            return;
        }

        string run = _lastRun;
        string world = MapGenCli.NewWorldProjectDir(run);
        var args = new List<string> { "export", "--run", run, "--world-project", world };
        SetBusy(true, "Regenerating to check the result hash…");
        Log("[b]Export[/b] Regenerating to check the result hash.");
        var (code, done, error) = await RunCommandAsync(args, j =>
        {
            if (EventName(j) == "pass" && j.GetProperty("enabled").GetBoolean())
                ShowProgress(j);
            else if (EventName(j) == "phase")
                _status.Text = j.GetProperty("name").GetString();
        });
        // Cancellation can reach the wrapper just after the CLI published both directories.
        // In that case the public receipts identify a completed export even without a done event.
        if (code == -1)
        {
            done = ReadPublishedExport(run, world);
            if (done.HasValue) code = 0;
        }

        OnUi(() =>
        {
            bool ok = code == 0 && done is JsonElement d && d.GetProperty("ok").GetBoolean()
                && d.GetProperty("hash").GetString() == _resultHash
                && d.GetProperty("world").GetProperty("hash").GetString() == _resultHash;
            if (ok)
            {
                JsonElement v = done.Value.GetProperty("verify");
                Log($"[b]Exported[/b] {v.GetProperty("land_cells_checked").GetInt32()} cells and {v.GetProperty("statics_found").GetInt32()} statics read back, 0 mismatches. "
                    + $"Map files: {Escape(Path.Combine(_lastRun, "export", "map"))}. World project: {Escape(world)}");
                _lastWorld = world;
                JsonElement generated = done.Value.GetProperty("world");
                JsonElement origin = generated.GetProperty("origin");
                _facet = generated.GetProperty("facet").GetInt32();
                _originX = origin[0].GetInt32();
                _originY = origin[1].GetInt32();
                _resultLabel.Text = "Verified export\n" + _resultText;
                ShowFinal();
            }
            SetBusy(false, ok ? "MUL files verified. Open World or click a cell on the final map."
                : code == -1 ? "Export cancelled. You can retry from this completed run."
                : error ?? (code == 0 ? "Export identity did not match the completed run." : $"Export failed (exit {code}). See Run details."));
        });
    }

    private static JsonElement? ReadPublishedExport(string run, string world)
    {
        string verification = Path.Combine(run, "export", "export-verify.json");
        string marker = Path.Combine(world, "generated.json");
        if (!File.Exists(verification) || !File.Exists(marker)) return null;
        try
        {
            using var verify = JsonDocument.Parse(File.ReadAllText(verification));
            using var generated = JsonDocument.Parse(File.ReadAllText(marker));
            return JsonSerializer.SerializeToElement(new
            {
                ok = verify.RootElement.GetProperty("ok").GetBoolean(),
                hash = verify.RootElement.GetProperty("hash").GetString(),
                verify = verify.RootElement,
                world = generated.RootElement,
            });
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private void OpenWorld()
    {
        if (!_busy && _lastWorld != null && OpenInWorld != null)
        {
            OpenInWorld(_lastWorld, _facet, _originX + _mapW / 2, _originY + _mapH / 2);
        }
    }

    private void SavePreset()
    {
        string name = MapGenCli.Slug(_savePresetName.Text);
        if (_busy || _lastRun == null || name.Length == 0)
        {
            _status.Text = "Name the preset first";
            return;
        }

        try
        {
            Directory.CreateDirectory(MapGenCli.UserPresetsDir);
            string path = Path.Combine(MapGenCli.UserPresetsDir, name + ".preset.json");
            File.Copy(Path.Combine(_lastRun, "preset.json"), path, overwrite: false);
            _ = LoadSchemaAsync(path);
        }
        catch (IOException ex) { _status.Text = $"Could not save preset '{name}'. {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { _status.Text = $"Could not save preset '{name}'. {ex.Message}"; }
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
        _showingStep = false;
        _layer.Disabled = false;
        _layer.SetItemText(0, _radarColours ? "Radar" : "Radar (biome colours)");
        _stepSlider.MaxValue = _steps.Count;
        _stepSlider.SetValueNoSignal(_steps.Count);
        _stepSlider.Editable = _steps.Count > 0;
        _stepLabel.Text = _steps.Count > 0 ? "Final map · drag left to inspect passes" : "Final map · no pass previews";
    }

    private void ShowStep(int i)
    {
        if (i < 0 || i >= _steps.Count)
        {
            return;
        }

        ShowImage(_steps[i].File);
        _showingStep = true;
        _layer.Disabled = true;
        _stepLabel.Text = $"After pass {_steps[i].Index + 1} · {_steps[i].Name}";
    }

    private void ShowImage(string path)
    {
        var clock = Stopwatch.StartNew();
        using Image img = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (img != null && !img.IsEmpty())
        {
            _preview.Texture = ImageTexture.CreateFromImage(img);
            _preview.Show();
            _empty.Hide();
            _cellLabel.Text = "";
            Log($"Preview {Escape(Path.GetFileName(path))} · {clock.Elapsed.TotalMilliseconds:0.0} ms");
        }
        else
            ClearPreview("The preview image is unavailable.");
    }

    private void ClearPreview(string message)
    {
        _preview.Texture = null;
        _preview.Hide();
        _empty.Text = message;
        _empty.Show();
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

        int x = PreviewCell(px.X, (int)tex.X, _mapW), y = PreviewCell(px.Y, (int)tex.Y, _mapH);
        bool handoff = !_showingStep && !_busy && _lastWorld != null && OpenInWorld != null;
        _cellLabel.Text = handoff ? $"{_facet}: {_originX + x}, {_originY + y}" : $"{x}, {y}";
        if (handoff && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            OpenInWorld(_lastWorld, _facet, _originX + x, _originY + y);
        }
    }

    // Match the source-cell interval averaged into each pixel by MapImage.Save.
    private static int PreviewCell(float position, int previewSize, int mapSize)
    {
        int pixel = (int)position;
        int start = pixel * mapSize / previewSize;
        int end = (pixel + 1) * mapSize / previewSize;
        return start + (int)((position - pixel) * (end - start));
    }

    private void ClearStats()
    {
        foreach (Node c in _likeness.GetChildren())
        {
            _likeness.RemoveChild(c);
            c.QueueFree();
        }
    }

    private void ShowStats(JsonElement stats)
    {
        ClearStats();
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
                any |= hit;
            }

            g.Root.Visible = any;
            if (q.Length > 0 && any)
            {
                g.Fold.ButtonPressed = true;
            }
        }
    }

    private void SetAllOpen(bool open)
    {
        foreach (PassGroup g in _passes)
        {
            g.Fold.ButtonPressed = open;
        }
    }

    private async Task<(int Code, JsonElement? Done, string Error)> RunCommandAsync(List<string> args, Action<JsonElement> onEvent)
    {
        _cancelSource = new CancellationTokenSource();
        _cancel.Show();
        _cancel.Disabled = false;
        JsonElement? done = null;
        string error = null;
        try
        {
            int code = await MapGenCli.RunAsync(args, j =>
            {
                switch (EventName(j))
                {
                    case "done": done = j; break;
                    case "error": error = j.GetProperty("message").GetString(); break;
                    default: OnUi(() => onEvent(j)); break;
                }
            }, line => OnUi(() => Log(Escape(line))), _cancelSource.Token);
            return (code, done, error);
        }
        catch (Exception ex)
        {
            OnUi(() => Log(Escape(ex.Message)));
            return (1, null, ex.Message);
        }
    }

    private static string EventName(JsonElement j) =>
        j.TryGetProperty("event", out JsonElement e) ? e.GetString() : "";

    private void OnUi(Action action)
    {
        if (_retired) return;
        Callable.From(() =>
        {
            if (!_retired) action();
        }).CallDeferred();
    }

    private void ShowProgress(JsonElement j)
    {
        int index = j.GetProperty("index").GetInt32() + 1, count = j.GetProperty("count").GetInt32();
        _progress.Indeterminate = false;
        _progress.MaxValue = count;
        _progress.Value = index - 1;
        _status.Text = $"Pass {index}/{count} · {j.GetProperty("name").GetString()}";
    }

    private void LogWarnings(JsonElement data)
    {
        if (!data.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array) return;
        foreach (var warning in warnings.EnumerateArray())
        {
            Log($"[color=orange]{Escape(warning.GetString())}[/color]");
            _warningCount++;
        }
        UpdateWarnings();
    }

    private void UpdateWarnings() =>
        _warningSummary.Text = _warningCount == 0 ? "" : _warningCount == 1 ? "1 warning" : $"{_warningCount} warnings";

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        if (!busy)
        {
            _cancelSource?.Dispose();
            _cancelSource = null;
        }
        _generate.Disabled = busy || !_schema.HasValue;
        _cancel.Visible = busy && _cancelSource != null;
        _progress.Visible = busy;
        _progress.Indeterminate = busy;
        _preset.Disabled = busy;
        _export.Disabled = busy || _lastRun == null || _lastWorld != null;
        _savePreset.Disabled = busy || _lastRun == null;
        _openWorld.Disabled = busy || _lastWorld == null || OpenInWorld == null;
        _restoreRun.Disabled = busy || _lastRun != null || _previous == null;
        _restoreRun.Visible = !_restoreRun.Disabled;
        _reloadSchema.Visible = !busy && !_schema.HasValue;
        _final.Disabled = _lastRun == null;
        _layer.Disabled = _lastRun == null || _showingStep;
        _status.Text = status;
        _status.TooltipText = status;
    }

    private void Log(string bbcode) => _log.AppendText(bbcode + "\n");

    private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static Label Heading(string text) => new() { Text = text, ThemeTypeVariation = "HeaderSmall" };

    /// <summary>Stops a run in progress (TearDown, before an assembly reload).</summary>
    public void Shutdown()
    {
        _retired = true;
        _cancelSource?.Cancel();
    }
}
#endif
