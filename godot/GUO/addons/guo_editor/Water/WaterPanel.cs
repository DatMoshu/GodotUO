// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Renderer.Water;
using GUO.Store;

/// <summary>Water workspace: reversible edits, comparison, synthetic interaction tests and portable packs.</summary>
[Tool]
internal partial class WaterPanel : ScrollContainer
{
    private StoreWater _style = WaterAuthoring.Presets[0];
    private sealed class DraftDocument
    {
        internal StoreWater Baseline;
        internal string Path;
        internal DraftDocument(StoreWater baseline, string path = null) { Baseline = baseline; Path = path; }
    }
    private DraftDocument _document = new(WaterAuthoring.Presets[0]);
    private StoreWater _baseline { get => _document.Baseline; set => _document.Baseline = value; }
    private string _draft { get => _document.Path; set => _document.Path = value; }
    private readonly List<(StoreWater Style, DraftDocument Document)> _history = new();
    private int _position = -1;
    private readonly Dictionary<string, SpinBox> _numbers = new();
    private LineEdit _name, _packId, _version, _author;
    private OptionButton _presets, _model, _licence;
    private ColorPickerButton _tint;
    private Label _status, _dirty, _modelInfo, _comparisonInfo, _nativeStatus;
    private Button _undo, _redo, _clearReference;
    private CheckBox _apply, _classic, _freeze, _compare;
    private WaterPreview _preview;
    private bool _sync;
    private Func<WorldHost> _host;
    private Func<bool> _worldVisible;
    private Action _worldStatus;
    private readonly WaterWorldTest _native = new();
    private Button _nativeBoat, _nativeWader, _nativeStop;
    private VBoxContainer _nativeSection;
    private GUO.Game.World _ownedWorld;
    private IWaterStyle _liveStyle;
    private readonly List<(StoreWater Style, string Path)> _drafts = new();
    internal StoreWater Style => _style;
    internal WaterPreview Preview => _preview;
    internal int HistoryPosition => _position;
    internal SpinBox NumberInput(string label) => _numbers[label];
    internal CheckBox WorldToggle => _apply;
    internal CheckBox ClassicToggle => _classic;
    internal CheckBox FreezeToggle => _freeze;
    internal CheckBox CompareToggle => _compare;
    internal string DraftPath => _draft;
    internal string StatusText => _status.Text;
    internal bool Dirty => !WaterAuthoring.Serialize(_style).AsSpan().SequenceEqual(WaterAuthoring.Serialize(_baseline));
    internal void Attach(Func<WorldHost> host, Func<bool> visible = null, Action status = null)
    { _host = host; _worldVisible = visible; _worldStatus = status; }
    internal WaterWorldTest NativeTest => _native;
    internal string NativeStatusText => _nativeStatus.Text;

    public override void _Ready()
    {
        Name = "Water";
        HorizontalScrollMode = ScrollMode.Disabled;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        TextureFilter = TextureFilterEnum.Nearest;
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8); AddChild(body);
        body.AddChild(new Label { Text = "Water authoring" });
        body.AddChild(new Label { Text = "Tune a preset, compare it, then save a pack.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var presetRow = new HBoxContainer(); body.AddChild(presetRow);
        _presets = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Starter presets and your saved drafts. Loading can be undone." };
        presetRow.AddChild(_presets);
        AddButton(presetRow, "Load", LoadSelected);
        AddButton(presetRow, "Refresh", RefreshPresets);
        BuildLibrary(body);
        BuildReplayControls(body);
        _preview = new WaterPreview(); body.AddChild(_preview);
        body.AddChild(new Label { Text = "Synthetic water • no client art", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var tests = new HFlowContainer(); body.AddChild(tests);
        AddButton(tests, "Test splash", () => _preview.Splash());
        Toggle(tests, "Moving hull", false, on => _preview.Boat = on);
        AddButton(tests, "Clear waves", () => _preview.ResetWaves());
        BuildPreviewExport(body);
        var viewing = new HFlowContainer(); body.AddChild(viewing);
        _compare = Toggle(viewing, "Compare", false, on => { _preview.Compare = on; UpdateReference(); });
        _classic = Toggle(viewing, "Classic", false, on => { _preview.Classic = on; UpdateReference(); UpdateWorld(); });
        _classic.TooltipText = "Temporarily show original tiles, retaining your draft.";
        _freeze = Toggle(viewing, "Freeze", false, on => _preview.Paused = on);
        var referenceActions = new HFlowContainer(); body.AddChild(referenceActions);
        AddButton(referenceActions, "Keep reference", () => Try(KeepReference)).TooltipText = "Keep this draft's settings on the left of Compare. Further edits affect only the right side. Restarts both ripple fields together.";
        _clearReference = AddButton(referenceActions, "Clear reference", ClearReference);
        _comparisonInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        body.AddChild(_comparisonInfo); UpdateReference();
        var sceneRow = new HBoxContainer(); body.AddChild(sceneRow);
        var scene = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (string s in new[] { "Open water", "Low beach", "High cliff" }) scene.AddItem(s);
        scene.ItemSelected += i => _preview.SetScene((int)i); sceneRow.AddChild(scene);
        var lighting = new OptionButton();
        foreach (string s in new[] { "Daylight", "Dusk", "Night" }) lighting.AddItem(s);
        lighting.ItemSelected += i => _preview.SetBrightness(i == 0 ? 1 : i == 1 ? .55f : .2f); sceneRow.AddChild(lighting);
        _apply = Toggle(body, "Preview in World", false, _ => UpdateWorld());
        _apply.TooltipText = "Uses the World view's wet tiles. No player setting or install data is saved.";
        _nativeSection = new VBoxContainer(); body.AddChild(_nativeSection);
        var nativeTests = new HFlowContainer(); _nativeSection.AddChild(nativeTests);
        _nativeBoat = AddButton(nativeTests, "Boat in World", () => Try(() => StartWorldTest(true)));
        _nativeWader = AddButton(nativeTests, "Wader in World", () => Try(() => StartWorldTest(false)));
        _nativeStop = AddButton(nativeTests, "Stop test", StopWorldTest);
        _nativeBoat.TooltipText = _nativeWader.TooltipText = "Find a clear water route near the current World view and focus a temporary native test actor. Uses the draft; no shard or project edits. Freeze pauses the test.";
        _nativeStatus = new Label { Text = "Native tests need the World view and client art.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _nativeSection.AddChild(_nativeStatus);
        UpdateNativeControls();

        var history = new HBoxContainer(); body.AddChild(history);
        _undo = AddButton(history, "Undo", Undo);
        _redo = AddButton(history, "Redo", Redo);
        AddButton(history, "Reset preset", () => Change(_baseline));
        _dirty = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill }; history.AddChild(_dirty);
        _name = new LineEdit { MaxLength = 48, PlaceholderText = "Preset name", TooltipText = "A name of up to 48 characters." }; body.AddChild(_name);
        _name.TextSubmitted += _ => Try(CommitName); _name.FocusExited += () => Try(CommitName);
        var modelRow = new HBoxContainer(); body.AddChild(modelRow);
        modelRow.AddChild(new Label { Text = "Model", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _model = new OptionButton();
        foreach (string name in new[] { "UO realtime", "Pixel", "Cel", "Realistic", "UO+" }) _model.AddItem(name);
        modelRow.AddChild(_model);
        _model.ItemSelected += i => { if (!_sync) Change(_style with { Model = StoreWater.Models[(int)i] }); };
        _modelInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        body.AddChild(_modelInfo);
        var tintRow = new HBoxContainer(); body.AddChild(tintRow);
        tintRow.AddChild(new Label { Text = "Deep water tint", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _tint = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Vector2(100, 28) }; tintRow.AddChild(_tint);
        _tint.ColorChanged += c => { if (!_sync) Change(_style with { Tint = new[] { c.R, c.G, c.B } }); };
        var waves = Group(body, "Waves & surface");
        Number(waves, "Speed", 0, 4, .05, s => s.Speed, (s, v) => s with { Speed = v }, "Animation speed. Zero stops ambient movement.");
        Number(waves, "Wave scale", .005, .25, .005, s => s.Scale, (s, v) => s with { Scale = v }, "Inverse wavelength: smaller values make broader waves.");
        Number(waves, "Wave strength", 0, 1, .01, s => s.Wave, (s, v) => s with { Wave = v });
        Number(waves, "Glints", 0, 1, .01, s => s.Glint, (s, v) => s with { Glint = v }, "Zero removes highlights. UO+ uses native texels and ignores glints.");
        Number(waves, "Palette levels", 0, 16, 1, s => s.Levels, (s, v) => s with { Levels = (int)v }, "Zero is smooth where the model supports it; Pixel and Cel retain their bands.");
        Number(waves, "Pixel size", 1, 8, 1, s => s.Grain, (s, v) => s with { Grain = (int)v }, "Whole native pixels per water pixel; sampling remains nearest.");
        var shore = Group(body, "Shore & interaction");
        Number(shore, "Shore foam", 0, 1, .01, s => s.Foam, (s, v) => s with { Foam = v }, "UO+ uses this for a quiet shore lap instead of white foam.");
        Number(shore, "Shore width", 0, .5, .01, s => s.FoamWidth, (s, v) => s with { FoamWidth = v }, "Width in world cells. High cliffs do not create beach surf.");
        Number(shore, "Ripple strength", 0, 2, .05, s => s.RippleStrength, (s, v) => s with { RippleStrength = v });
        Number(shore, "Wave retention", .9, .999, .001, s => s.RippleDamping, (s, v) => s with { RippleDamping = v }, "Amount retained each simulation step. Higher values ring longer.");

        var drafts = Group(body, "Save & share");
        var draftActions = new HFlowContainer(); drafts.AddChild(draftActions);
        AddButton(draftActions, "Save draft", SaveDraft);
        AddButton(draftActions, "Duplicate", Duplicate);
        AddButton(draftActions, "Import JSON…", ImportDialog);
        _packId = Entry(drafts, "Pack ID", "my-water-style");
        _version = Entry(drafts, "Version", "1.0.0");
        _author = Entry(drafts, "Author", "");
        var licenceRow = new HBoxContainer(); drafts.AddChild(licenceRow);
        licenceRow.AddChild(new Label { Text = "Licence", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _licence = new OptionButton(); foreach (string s in new[] { "CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0", "MIT", "BSD-2-Clause" }) _licence.AddItem(s);
        licenceRow.AddChild(_licence);
        AddButton(drafts, "Export water pack…", ExportDialog);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; body.AddChild(_status);
        _history.Add((_style, _document)); _position = 0; Sync(); RefreshPresets();
        SetProcess(true);
    }

    private static Button AddButton(Node parent, string text, Action run)
    {
        var button = new Button { Text = text }; button.Pressed += run; parent.AddChild(button); return button;
    }
    private static CheckBox Toggle(Node parent, string text, bool value, Action<bool> change)
    {
        var box = new CheckBox { Text = text, ButtonPressed = value }; parent.AddChild(box); box.Toggled += on => change(on); return box;
    }
    private static VBoxContainer Group(Node parent, string title)
    {
        var box = new VBoxContainer(); parent.AddChild(new HSeparator()); parent.AddChild(new Label { Text = title }); parent.AddChild(box); return box;
    }
    private static LineEdit Entry(Node parent, string label, string value)
    {
        var row = new HBoxContainer(); parent.AddChild(row);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) });
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(edit); return edit;
    }
    private readonly Dictionary<string, Func<StoreWater, float>> _read = new();
    private void Number(Node parent, string title, double min, double max, double step,
        Func<StoreWater, float> read, Func<StoreWater, float, StoreWater> write, string tip = "")
    {
        var row = new HBoxContainer { TooltipText = tip }; parent.AddChild(row);
        var label = new Label { Text = title, SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(label);
        var input = new SpinBox { MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(105, 0) };
        row.AddChild(input); _numbers[title] = input; _read[title] = read;
        input.ValueChanged += v => { if (!_sync && input.Editable) ChangeNumber(title, write(_style, (float)v)); };
        AttachWaterScrub(label, input, title);
    }
    private void CommitName() { if (!_sync) Change(_style with { Name = _name.Text.Trim() }); }

    internal void KeepReference()
    {
        CommitName();
        SetReference(_style);
        _compare.ButtonPressed = true;
        UpdateReference();
    }
    internal void ClearReference() => SetReference(null);
    internal void SetReference(StoreWater style)
    {
        _preview.SetReference(style);
        UpdateReference();
    }
    private void UpdateReference()
    {
        if (_comparisonInfo == null) return;
        string left = _preview.ReferenceStyle?.Name ?? "Classic synthetic texels";
        _compare.TooltipText = $"Left: {left}. Right: the current draft. Only the synthetic preview is split.";
        _comparisonInfo.Text = $"Left: {left}\nRight: current draft";
        _comparisonInfo.Visible = _compare.ButtonPressed && !_classic.ButtonPressed;
        _clearReference.Disabled = _preview.ReferenceStyle == null;
    }

    internal void Change(StoreWater style)
        => Push(style, _document);

    private void Push(StoreWater style, DraftDocument document, bool refresh = true)
    {
        EndNumericScrub();
        WaterAuthoring.Serialize(style);
        if (ReferenceEquals(document, _document)
            && WaterAuthoring.Serialize(style).AsSpan().SequenceEqual(WaterAuthoring.Serialize(_style))) return;
        _style = style; _document = document;
        _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add((style, document)); if (_history.Count > 64) _history.RemoveAt(0);
        _position = _history.Count - 1; if (refresh) Sync();
    }
    private void Restore(int position)
    {
        _position = position; (_style, _document) = _history[position]; Sync();
    }
    internal void Undo() { EndNumericScrub(); if (_position > 0) Restore(_position - 1); }
    internal void Redo() { EndNumericScrub(); if (_position + 1 < _history.Count) Restore(_position + 1); }
    internal void LoadStyle(StoreWater style, string path = null)
    {
        Push(style, new DraftDocument(style, path));
        int index = Array.FindIndex(WaterAuthoring.Presets, p => p.Name == style.Name);
        if (index >= 0) _presets.Select(index);
    }
    internal void Duplicate() => LoadStyle(_style with { Name = (_style.Name.Length > 42 ? _style.Name[..42] : _style.Name) + " copy" });
    private void Sync()
    {
        if (_name == null) return;
        _sync = true;
        _name.Text = _style.Name; _model.Select(Array.IndexOf(StoreWater.Models, _style.Model));
        _tint.Color = new Color(_style.Tint[0], _style.Tint[1], _style.Tint[2]);
        foreach (var (key, spin) in _numbers) spin.SetValueNoSignal(_read[key](_style));
        bool native = _style.Model == "classicplus";
        _tint.Disabled = native;
        SetNumberEnabled("Wave scale", !native);
        SetNumberEnabled("Glints", !native);
        SetNumberEnabled("Palette levels", !native && _style.Model != "realistic");
        _modelInfo.Text = _style.Model switch
        {
            "classicplus" => "UO+ keeps native texels and colours. Tint, wave scale, glints and palette levels are unused; their values are retained when you switch models.",
            "realistic" => "Smooth lighting and soft surf. Palette levels are unused. Compatibility omits fine chop and glitter.",
            "cel" => "Tinted colour bands and crisp ripple rings. Shore foam of 0.01 or higher enables outlined surf. At least two colour bands remain.",
            "pixel" => "Dithered light and whole-pixel glints. Palette levels adjust the light bands; the short colour ramp remains.",
            _ => "Native tile colour with animated swell and ripple highlights. Palette levels at zero gives smooth light."
        };
        _undo.Disabled = _position <= 0 && !ScrubChanged;
        _redo.Disabled = ScrubChanged || _position + 1 >= _history.Count;
        _dirty.Text = Dirty ? "Modified" : "Unmodified";
        _preview.SetStyle(_style);
        _liveStyle = new PackWaterStyle("editor-draft", _style);
        _sync = false; UpdateWorld();
    }
    private void SetNumberEnabled(string key, bool enabled)
    {
        SpinBox input = _numbers[key];
        input.Editable = enabled;
        var row = (Control)input.GetParent();
        var label = row.GetChild<Label>(0);
        label.MouseFilter = enabled ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        label.MouseDefaultCursorShape = enabled ? CursorShape.Hsize : CursorShape.Arrow;
        row.Modulate = new Color(1, 1, 1, enabled ? 1 : .45f);
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { _status.Text = "Could not complete water action: " + ex.Message; }
    }
    internal string DraftFolderOverride;
    private string DraftFolder => DraftFolderOverride ?? WaterAuthoring.DraftDirectory;
    internal int DraftCount => _drafts.Count;
    internal void RefreshPresets() => Try(() =>
    {
        int selected = _presets.Selected;
        string selectedPath = selected >= WaterAuthoring.Presets.Length
            && selected - WaterAuthoring.Presets.Length < _drafts.Count
            ? _drafts[selected - WaterAuthoring.Presets.Length].Path : null;
        _presets.Clear(); _drafts.Clear();
        foreach (var style in WaterAuthoring.Presets) _presets.AddItem(style.Name);
        int unreadable = 0;
        foreach (string path in Directory.Exists(DraftFolder)
            ? Directory.EnumerateFiles(DraftFolder, "*.json").OrderBy(p => p).Take(256) : Array.Empty<string>())
        {
            try { var style = WaterAuthoring.Read(path); _drafts.Add((style, path)); _presets.AddItem(style.Name + " (draft)"); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { unreadable++; }
        }
        int savedIndex = selectedPath == null ? -1 : _drafts.FindIndex(d => d.Path == selectedPath);
        _presets.Select(savedIndex >= 0 ? WaterAuthoring.Presets.Length + savedIndex
            : selected >= 0 && selected < WaterAuthoring.Presets.Length ? selected : 0);
        if (unreadable > 0) _status.Text = $"Skipped {unreadable} unreadable draft(s). Import a corrected JSON file to try again.";
        RefreshLibrary(unreadable);
    });
    private void LoadSelected() => Try(() =>
    {
        int index = _presets.Selected;
        if (index < 0) return;
        if (index < WaterAuthoring.Presets.Length) LoadStyle(WaterAuthoring.Presets[index]);
        else { string path = _drafts[index - WaterAuthoring.Presets.Length].Path; LoadStyle(WaterAuthoring.Read(path), path); }
        _status.Text = "Preset loaded. Undo restores the previous draft.";
    });
    private void SaveDraft() => Try(() => SaveWorkingDraft());
    internal void SaveWorkingDraft()
    {
        CommitName();
        if (_draft != null && (!File.Exists(_draft)
            || !WaterAuthoring.Serialize(WaterAuthoring.Read(_draft)).AsSpan().SequenceEqual(WaterAuthoring.Serialize(_baseline))))
            throw new IOException("This draft changed or was removed on disk. Load the saved draft to review it, or Duplicate to save your edits separately.");
        string path = _draft ?? Path.Combine(DraftFolder, Guid.NewGuid().ToString("N") + ".json");
        WaterAuthoring.Save(_style, path); _draft = path; _baseline = _style; Sync(); RefreshPresets();
        _status.Text = "Draft saved: " + _draft;
    }
    internal void ImportPath(string path) => Try(() =>
    { LoadStyle(WaterAuthoring.Read(path)); _status.Text = "Imported a local draft. Save draft to keep a working copy."; });
    private FileDialog Dialog(string title, FileDialog.FileModeEnum mode, string filter)
    {
        var dialog = new FileDialog { Title = title, Access = FileDialog.AccessEnum.Filesystem, FileMode = mode };
        dialog.AddFilter(filter); AddChild(dialog); dialog.Canceled += () => dialog.QueueFree(); return dialog;
    }
    private void ImportDialog()
    {
        var dialog = Dialog("Import water preset", FileDialog.FileModeEnum.OpenFile, "*.json ; Water preset");
        dialog.FileSelected += path => { ImportPath(path); dialog.QueueFree(); };
        dialog.PopupCenteredRatio(.65f);
    }
    private void ExportDialog() => Try(() =>
    {
        CommitName(); StorePack.Id(_packId.Text.Trim()); StorePack.Version(_version.Text.Trim());
        if (string.IsNullOrWhiteSpace(_author.Text)) throw new InvalidDataException("Enter an author before exporting.");
        var dialog = Dialog("Export water pack", FileDialog.FileModeEnum.SaveFile, "*.zip ; Water Store pack");
        dialog.CurrentFile = _packId.Text.Trim() + "-" + _version.Text.Trim() + ".zip";
        dialog.FileSelected += path =>
        {
            Try(() => { WaterAuthoring.Export(_style, path, _packId.Text.Trim(), _version.Text.Trim(), _author.Text.Trim(), _licence.GetItemText(_licence.Selected)); _status.Text = "Water pack exported: " + path; });
            dialog.QueueFree();
        };
        dialog.PopupCenteredRatio(.65f);
    });

    private void UpdateWorld()
    {
        UpdateReplayControls();
        var world = _apply?.ButtonPressed == true ? _host?.Invoke()?.World : null;
        if (world == null && _native.Active) StopWorldTest();
        if (_preview != null) _preview.LiveWorld = world != null;
        if (_ownedWorld != null && world != _ownedWorld && WaterRender.EditorWorld == _ownedWorld)
        { WaterRender.EditorWorld = null; WaterRender.EditorStyle = null; }
        _ownedWorld = world;
        if (world == null) return;
        WaterRender.EditorWorld = world;
        WaterRender.EditorStyle = _classic.ButtonPressed ? null : _liveStyle;
        WaterRender.EditorSeconds = _preview.Seconds;
        WaterRender.EditorPaused = _freeze.ButtonPressed;
    }
    internal void StartWorldTest(bool boat)
    {
        _apply.ButtonPressed = true; UpdateWorld();
        _nativeStatus.Text = _native.Start(_host?.Invoke(), boat);
        _worldStatus?.Invoke();
        UpdateNativeControls();
    }
    internal void StopWorldTest()
    {
        _native.Stop();
        _worldStatus?.Invoke();
        if (_nativeStatus != null) _nativeStatus.Text = "Test actor removed.";
        UpdateNativeControls();
    }
    private void UpdateNativeControls()
    {
        if (_nativeBoat == null) return;
        bool ready = _host?.Invoke()?.IsBooted == true;
        _nativeSection.Visible = _apply.ButtonPressed;
        _nativeBoat.Disabled = _nativeWader.Disabled = !ready;
        _nativeStop.Disabled = !_native.Active;
        if (ready && _nativeStatus.Text == "Native tests need the World view and client art.")
            _nativeStatus.Text = "Temporary native actors; scripted paths, no gameplay collision test.";
    }
    public override void _Process(double delta)
    {
        if (_scrubKey != null && (!IsVisibleInTree() || !Input.IsMouseButtonPressed(MouseButton.Left))) EndNumericScrub();
        if (_native.Active && _worldVisible?.Invoke() == false) StopWorldTest();
        UpdateWorld();
        bool active = _native.Active;
        _native.Advance((float)delta, _freeze.ButtonPressed);
        if (active && !_native.Active) _worldStatus?.Invoke();
        if (active && !_native.Active) _nativeStatus.Text = "Native test stopped because the World view changed. Start another test here when ready.";
        UpdateNativeControls();
    }
    internal void Shutdown()
    {
        // Child previews may already have exited and disposed their renderer resources.
        FinishNumericScrub(false);
        SetProcess(false);
        StopWorldTest();
        if (_ownedWorld != null && WaterRender.EditorWorld == _ownedWorld) { WaterRender.EditorWorld = null; WaterRender.EditorStyle = null; }
        _ownedWorld = null; _preview?.Shutdown();
    }
    public override void _ExitTree() => Shutdown();
}
#endif
