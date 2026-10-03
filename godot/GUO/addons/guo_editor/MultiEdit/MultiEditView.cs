#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;

/// <summary>A component a generator hands to the editor's canvas.</summary>
public readonly record struct GeneratedPart(ushort Id, int X, int Y, int Z, bool Shown = true, ushort Hue = 0);

/// <summary>
/// The seam for generators (ADR-0031): anything that makes a component list (the style catalogue's
/// generator panel, a script) pushes it here. With <c>replace</c> it becomes the document's contents,
/// otherwise it is added to them; either way it is one undo step named after the generator.
/// </summary>
public interface IMultiComponentSink
{
    void PushComponents(string name, IReadOnlyList<GeneratedPart> parts, bool replace);
}

/// <summary>
/// The Multi Editor main-screen tab (ADR-0031): canvas, palette, stories and vision modes, tools, history,
/// parts and problems lists, and the way into a staged data set. It never writes the install.
/// </summary>
[Tool]
public partial class MultiEditView : VBoxContainer, IMultiComponentSink
{
    public const string TabName = "Multis";

    private EditorData _data;
    private MultiDocument _doc;
    private MultiCanvas _canvas;
    private MultiPalette _palette;
    private HouseTables _tables;
    private ValidationResult _result = new();
    private TabContainer _tabs;
    private ItemList _historyList, _partsList, _problemsList;
    private Label _status, _hint, _selection, _summary;
    private LineEdit _name, _stage, _openId;
    private Label _saveLog;
    private SpinBox _zSpin, _zMin, _zMax;
    private readonly Dictionary<MultiTool, Button> _toolButtons = new();
    private readonly OptionButton[] _vision = new OptionButton[Stories.Max];
    private readonly Button[] _storyButtons = new Button[5];
    private CheckBox _grid, _floor, _walk, _problems, _hidden, _cut;
    private OptionButton _brush;
    private Button _undo, _redo;
    private bool _dirty;
    private double _since;
    private bool _built;
    private bool _syncing;
    private int _stamp;

    public MultiDocument Doc => _doc;
    public MultiCanvas Canvas => _canvas;
    public MultiPalette Palette => _palette;
    internal HouseTables Tables => _tables;
    public ValidationResult Result => _result;
    public string StageDir => _stage?.Text.Trim() is { Length: > 0 } s ? s : MultiStore.DefaultStage;

    /// <summary>Raised after a successful write to a stage, for the plugin to refresh the Multis panel and the World tab.</summary>
    public Action<SaveResult> AfterWrite { get; set; }

    /// <summary>Asked for by the Preview button: place this multi id in the World tab.</summary>
    public Action<int> PreviewInWorld { get; set; }

    /// <summary>The generator seam: the view itself.</summary>
    public IMultiComponentSink Sink => this;

    /// <summary>Points the Save tab at another staged data set (the smoke check uses its own folder).</summary>
    public void SetStageDir(string dir)
    {
        _stage.Text = dir;
    }

    public void SetName(string name)
    {
        _name.Text = name;
    }

    public MultiEditView() : this(null)
    {
    }

    public MultiEditView(EditorData data)
    {
        _data = data;
        Name = "MultiEditor";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // Not Visible = false here: an assembly reload recreates this object through the parameterless
        // constructor (see WorldView); the plugin hides it.
    }

    public override void _Ready()
    {
        if (_built || _data == null)
        {
            return;
        }

        _built = true;
        _doc = new MultiDocument();
        _doc.Changed += OnDocChanged;
        Build();
        MarkSaved();
        if (_data.IsLoaded)
        {
            OnDataLoaded();
        }
        else
        {
            _data.Loaded += OnDataLoaded;
        }

        _data.AssetsApplied += OnAssetsApplied;
    }

    private void OnAssetsApplied() => _canvas?.ForgetArt();

    public void OnDataLoaded()
    {
        if (!_data.IsLoaded || _canvas == null)
        {
            return;
        }

        try
        {
            _tables = HouseTables.Load(_data.Files);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] multi editor tables: {ex.GetType().Name}: {ex.Message}");
            _tables = new HouseTables();
        }

        _canvas.Attach(_data, _doc, _tables);
        _palette.Attach(_data, _tables);
        LoadStagedOverlay();
        _summary.Text = $"client tables: {_tables.Groups.Count} groups" + (_tables.Missing.Count > 0 ? $" (missing {string.Join(", ", _tables.Missing)})" : "");
        _canvas.FitView();
        ValidateNow();
    }

    // --- building the UI -------------------------------------------------------------------

    private Button Tip(Button b, string tip)
    {
        b.TooltipText = tip;
        return b;
    }

    private void Build()
    {
        // Row 1: files, tools, history.
        var bar = new HBoxContainer();
        AddChild(bar);
        bar.AddChild(Tip(Btn("New", () => GuardUnsaved(NewMulti)), "A blank multi (the history starts again; asks first when there are unsaved changes)"));
        _openId = new LineEdit { PlaceholderText = "client multi id", CustomMinimumSize = new Vector2(110, 0), TooltipText = "0x0064 or 100" };
        _openId.TextSubmitted += _ => OpenTyped();
        bar.AddChild(_openId);
        bar.AddChild(Tip(Btn("Open", OpenTyped), "Open a client multi by id"));
        BuildFormatMenus(bar);
        bar.AddChild(new VSeparator());

        var group = new ButtonGroup();
        foreach ((MultiTool t, string label, string key) in new[]
        {
            (MultiTool.Select, "Select", "S"), (MultiTool.Draw, "Draw", "D"), (MultiTool.Erase, "Erase", "E"), (MultiTool.Pipette, "Pipette", "I"),
            (MultiTool.Rect, "Rect", "R"), (MultiTool.Line, "Line", "L"), (MultiTool.Brush, "Brush", "B"), (MultiTool.Move, "Move", "M"),
            (MultiTool.WallRun, "Wall", "W"), (MultiTool.Roof, "Roof", "O"), (MultiTool.Stairs, "Stairs", "T"),
        })
        {
            MultiTool tool = t;
            var b = new Button { Text = label, ToggleMode = true, ButtonGroup = group, TooltipText = $"{label} ({key})", ButtonPressed = t == MultiTool.Select };
            b.Pressed += () => SetTool(tool);
            _toolButtons[t] = b;
            bar.AddChild(b);
        }

        _brush = new OptionButton { TooltipText = "Brush size" };
        foreach (string s in new[] { "1x1", "3x3", "5x5" })
        {
            _brush.AddItem(s);
        }

        _brush.ItemSelected += i => _canvas.BrushRadius = (int)i;
        bar.AddChild(_brush);
        bar.AddChild(new VSeparator());
        _undo = Btn("Undo", () => _doc.Undo());
        _undo.TooltipText = "Ctrl+Z";
        bar.AddChild(_undo);
        _redo = Btn("Redo", () => _doc.Redo());
        _redo.TooltipText = "Ctrl+Y";
        bar.AddChild(_redo);
        bar.AddChild(new VSeparator());
        bar.AddChild(Tip(Btn("Fit", () => _canvas.FitView()), "Centre the multi (Home)"));
        bar.AddChild(Tip(Btn("-", () => _canvas.ZoomBy(-1)), "Zoom out"));
        bar.AddChild(Tip(Btn("+", () => _canvas.ZoomBy(1)), "Zoom in (nearest sampling; the wheel zooms at the pointer)"));
        bar.AddChild(new VSeparator());
        bar.AddChild(Tip(Btn("Preview in World", () => PreviewNow()), "Place the last written multi in the World tab"));
        BuildTitle(bar);
        _summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        bar.AddChild(_summary);

        // Row 2: the selection.
        var sel = new HBoxContainer();
        AddChild(sel);
        _selection = new Label { Text = "nothing selected", CustomMinimumSize = new Vector2(150, 0) };
        sel.AddChild(_selection);
        sel.AddChild(Tip(Btn("All", SelectAll), "Select every visible component (Ctrl+A)"));
        sel.AddChild(Tip(Btn("Hue", () => _doc.SetHue(_doc.Selection.ToList(), _palette.Hue)), "Give the selection the palette's hue"));
        sel.AddChild(Tip(Btn("Hide/show", ToggleShown), "Toggle the Shown flag on the selection (H)"));
        sel.AddChild(Tip(Btn("Delete", () => _doc.Remove(_doc.Selection.ToList(), $"delete {_doc.Selection.Count} component(s)")), "Delete"));
        foreach (int dz in new[] { -5, -1, 1, 5 })
        {
            int d = dz;
            sel.AddChild(Tip(Btn($"z{(d > 0 ? "+" : "")}{d}", () => _canvas.NudgeZ(d)), "Group z: moves the selection, or the editing z with none ([ ] PgUp PgDn)"));
        }

        BuildTransformButtons(sel);
        BuildClipboardButtons(sel);
        sel.AddChild(new VSeparator());
        _hint = new Label
        {
            Text = "S select  D draw  E erase  I pipette  R rect  L line  B brush  M move  W wall  O roof  T stairs   [ ] z   arrows nudge   G grid  F floor",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        sel.AddChild(_hint);

        // Body: palette | stories + canvas | tabs.
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        _palette = new MultiPalette();
        split.AddChild(_palette);

        var middle = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(middle);
        middle.AddChild(BuildStories());
        _canvas = new MultiCanvas();
        middle.AddChild(_canvas);

        _tabs = new TabContainer { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        split.AddChild(_tabs);
        BuildTabs();
        BuildGenerator();
        BuildClipboard();

        _status = new Label { Text = "", ClipText = true };
        AddChild(_status);

        // Wiring.
        _canvas.Status += s => _status.Text = s;
        _canvas.Pipetted += (id, hue) =>
        {
            _palette.Choose(id);
            _palette.SetHue(hue);
            SetTool(MultiTool.Draw);
        };
        _canvas.TileDropped += id =>
        {
            _palette.Choose(id);
        };
        _canvas.EditZChanged += z =>
        {
            _syncing = true;
            _zSpin.Value = z;
            _syncing = false;
            SyncStoryButtons();
        };
        _canvas.SelectionEdited += UpdateSelectionUi;
        _canvas.ToolKeyPressed += SyncFromCanvas;
        _canvas.SaveRequested += () => SaveDescription();
        _palette.TileChanged += id => _canvas.TileId = id;
        _palette.HueChanged += h => _canvas.TileHue = h;
        SetProcess(true);
    }

    private Button Btn(string text, Action run)
    {
        var b = new Button { Text = text };
        b.Pressed += () => run();
        return b;
    }

    private Control BuildStories()
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        box.AddChild(new Label { Text = "Stories (z 7 + 20 n)" });
        string[] names = { "Foundation  z0", "Story 1  z7", "Story 2  z27", "Story 3  z47", "Story 4  z67" };
        var group = new ButtonGroup();
        for (int i = 0; i < 5; i++)
        {
            int story = i - 1;
            var row = new HBoxContainer();
            var b = new Button { Text = names[i], ToggleMode = true, ButtonGroup = group, CustomMinimumSize = new Vector2(100, 0), ButtonPressed = story == 0, TooltipText = "Edit at this story's floor z (key " + (story < 0 ? 0 : story + 1) + ")" };
            b.Pressed += () => _canvas.SetEditZ(story < 0 ? 0 : Stories.ZOf(story));
            _storyButtons[i] = b;
            row.AddChild(b);
            if (story >= 0)
            {
                var v = new OptionButton { TooltipText = "The client's vision mode for this story", CustomMinimumSize = new Vector2(70, 0) };
                foreach (string m in new[] { "Normal", "Transp. content", "Hide content", "Transp. floor", "Hide floor", "Transl. floor", "Hide all" })
                {
                    v.AddItem(m);
                }

                v.ItemSelected += idx =>
                {
                    _canvas.Vision[story] = (StoryVision)(int)idx;
                    _canvas.Touch();
                };
                _vision[story] = v;
                row.AddChild(v);
            }

            box.AddChild(row);
        }

        var zrow = new HBoxContainer();
        zrow.AddChild(new Label { Text = "edit z" });
        _zSpin = new SpinBox { MinValue = -128, MaxValue = 127, Value = Stories.FloorZ, TooltipText = "Free z of what you draw ([ ] +-1, PgUp PgDn +-5)" };
        _zSpin.ValueChanged += v =>
        {
            if (!_syncing)
            {
                _canvas.SetEditZ((int)v);
            }
        };
        zrow.AddChild(_zSpin);
        box.AddChild(zrow);

        _cut = Check(box, "Cut above this story", false, v =>
        {
            _canvas.CutAbove = v;
            _canvas.Touch();
        });
        _grid = Check(box, "Grid (G)", true, v => { _canvas.ShowGrid = v; _canvas.Touch(); });
        _floor = Check(box, "Virtual floor (F)", true, v => { _canvas.ShowVirtualFloor = v; _canvas.Touch(); });
        _walk = Check(box, "Walkable surfaces", true, v => { _canvas.ShowWalkable = v; _canvas.Touch(); });
        _problems = Check(box, "Problems overlay", true, v => { _canvas.ShowProblems = v; _canvas.Touch(); });
        _hidden = Check(box, "Show hidden components", true, v => { _canvas.ShowHidden = v; _canvas.Touch(); });
        Check(box, "Draw replaces the cell at z", false, v => _canvas.ReplaceCell = v);

        var zr = new HBoxContainer();
        zr.AddChild(new Label { Text = "z range" });
        _zMin = new SpinBox { MinValue = -128, MaxValue = 127, Value = -128 };
        _zMax = new SpinBox { MinValue = -128, MaxValue = 127, Value = 127 };
        _zMin.ValueChanged += v => { _canvas.ZMin = (int)v; _canvas.Touch(); };
        _zMax.ValueChanged += v => { _canvas.ZMax = (int)v; _canvas.Touch(); };
        zr.AddChild(_zMin);
        zr.AddChild(_zMax);
        box.AddChild(zr);
        return box;
    }

    private static CheckBox Check(Control parent, string text, bool on, Action<bool> set)
    {
        var c = new CheckBox { Text = text, ButtonPressed = on };
        c.Toggled += v => set(v);
        parent.AddChild(c);
        return c;
    }

    private void BuildTabs()
    {
        _partsList = new ItemList { Name = "Parts", SelectMode = ItemList.SelectModeEnum.Multi, SizeFlagsVertical = SizeFlags.ExpandFill, TextureFilter = TextureFilterEnum.Nearest };
        _partsList.MultiSelected += (_, _) => SelectFromParts();
        _tabs.AddChild(_partsList);

        _historyList = new ItemList { Name = "History", SizeFlagsVertical = SizeFlags.ExpandFill };
        _historyList.ItemSelected += i => _doc.JumpTo((int)i);
        _tabs.AddChild(_historyList);

        _problemsList = new ItemList { Name = "Problems", SizeFlagsVertical = SizeFlags.ExpandFill };
        _problemsList.ItemSelected += OnProblem;
        _tabs.AddChild(_problemsList);

        var save = new VBoxContainer { Name = "Save" };
        _tabs.AddChild(save);
        save.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { Text = "new_multi" };
        save.AddChild(_name);
        save.AddChild(new Label { Text = "Staged data set (never the install)" });
        _stage = new LineEdit { Text = MultiStore.DefaultStage };
        save.AddChild(_stage);
        save.AddChild(Btn("Save description", () => SaveDescription()));
        save.AddChild(Btn("Write to stage", async () => await SaveToStageAsync()));
        save.AddChild(Btn("Open description...", OpenDescriptionDialog));
        _saveLog = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
        save.AddChild(_saveLog);
        save.AddChild(new Label
        {
            Text = "A multi record has no hue: the description keeps it, the stage does not. The writer only adds; a changed multi under a used name is saved as name-2.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.6f),
        });
    }

    /// <summary>Mounts another panel (a generator, say) in the right-hand tabs.</summary>
    public void AddSidePanel(string title, Control panel)
    {
        panel.Name = title;
        _tabs.AddChild(panel);
    }

    public IReadOnlyList<string> TabNames => _tabs.GetChildren().Select(c => c.Name.ToString()).ToList();

    // --- tools and state sync ---------------------------------------------------------------------

    public void SetTool(MultiTool tool)
    {
        _canvas.Tool = tool;
        if (tool is MultiTool.WallRun or MultiTool.Roof or MultiTool.Stairs)
        {
            ShowGenerateTab();
            _genPanel.SelectGenerator(tool == MultiTool.WallRun ? "autowall" : tool == MultiTool.Roof ? "roof" : "stairs");
            RefreshToolContext();
        }

        if (_toolButtons.TryGetValue(tool, out Button b))
        {
            b.SetPressedNoSignal(true);
        }

        _canvas.QueueRedraw();
    }

    public void SetVision(int story, StoryVision mode)
    {
        _canvas.Vision[story] = mode;
        _vision[story]?.Select((int)mode);
        _canvas.Touch();
    }

    private void SyncFromCanvas()
    {
        SetTool(_canvas.Tool);
        _grid.SetPressedNoSignal(_canvas.ShowGrid);
        _floor.SetPressedNoSignal(_canvas.ShowVirtualFloor);
        UpdateSelectionUi();
    }

    private void SyncStoryButtons()
    {
        int s = Stories.StoryOf(_canvas.EditZ);
        for (int i = 0; i < _storyButtons.Length; i++)
        {
            _storyButtons[i].SetPressedNoSignal(i - 1 == s && (s >= 0 ? _canvas.EditZ == Stories.ZOf(s) : _canvas.EditZ == 0));
        }
    }

    private void ToggleShown()
    {
        if (_doc.Selection.Count > 0)
        {
            bool anyShown = _doc.Parts.Any(p => _doc.Selection.Contains(p.Uid) && p.Shown);
            _doc.SetShown(_doc.Selection.ToList(), !anyShown);
        }
    }

    private void SelectAll()
    {
        _doc.Selection.Clear();
        foreach (MultiPart p in _doc.Parts.Where(p => _canvas.Display(p).Visible))
        {
            _doc.Selection.Add(p.Uid);
        }

        UpdateSelectionUi();
        _canvas.QueueRedraw();
    }

    private void UpdateSelectionUi()
    {
        _selection.Text = _doc.Selection.Count == 0 ? "nothing selected" : $"{_doc.Selection.Count} selected";
        _canvas.QueueRedraw();
    }

    // --- document events ------------------------------------------------------------------------------

    private void OnDocChanged()
    {
        _dirty = true;
        _since = 0;
        _stamp++;
        UpdateTitle();
    }

    public override void _Process(double delta)
    {
        if (!_dirty || !Visible)
        {
            return;
        }

        _since += delta;
        if (_since > 0.2)
        {
            ValidateNow();
        }
    }

    /// <summary>Validates now and refreshes every list. The smoke check calls this instead of waiting for the idle timer.</summary>
    public ValidationResult ValidateNow()
    {
        _dirty = false;
        if (_data == null || !_data.IsLoaded || _doc == null)
        {
            return _result;
        }

        _result = MultiValidator.Run(_doc.Parts, _data.Files.TileData.StaticData, id => _data.HasArt(EditorData.LandCount + id), _tables);
        _canvas.SetResult(_result);
        RefreshLists();
        return _result;
    }

    private void RefreshLists()
    {
        _historyList.Clear();
        IReadOnlyList<string> names = _doc.HistoryNames;
        for (int i = 0; i < names.Count; i++)
        {
            _historyList.AddItem($"{i}  {names[i]}");
        }

        if (names.Count > 0)
        {
            _historyList.Select(_doc.Cursor, false);
            _historyList.EnsureCurrentIsVisible();
        }

        _undo.Disabled = !_doc.CanUndo;
        _redo.Disabled = !_doc.CanRedo;

        _partsList.Clear();
        StaticTiles[] tiles = _data.Files.TileData.StaticData;
        int shown = 0;
        foreach (MultiPart p in _doc.Parts)
        {
            if (shown++ >= 1500)
            {
                _partsList.AddItem($"... {_doc.Parts.Count - 1500} more");
                break;
            }

            string n = p.Id < tiles.Length ? tiles[p.Id].Name : "?";
            int i = _partsList.AddItem($"0x{p.Id:X4} {p.X},{p.Y},{p.Z}{(p.Shown ? "" : " hidden")}{(p.Hue != 0 ? $" hue {p.Hue}" : "")}  {n}");
            _partsList.SetItemMetadata(i, p.Uid);
            if (_doc.Selection.Contains(p.Uid))
            {
                _partsList.Select(i, false);
            }
        }

        _problemsList.Clear();
        foreach (Finding f in _result.Findings.OrderByDescending(f => f.Severity))
        {
            int i = _problemsList.AddItem($"[{f.Severity}] {f.Kind}: {f.Message}");
            _problemsList.SetItemMetadata(i, f.Uid >= 0 ? f.Uid : -1 - (f.X + 1000) * 4096 - (f.Y + 1000));
        }

        int errors = _result.Findings.Count(f => f.Severity == FindingSeverity.Error);
        int warnings = _result.Findings.Count(f => f.Severity == FindingSeverity.Warning);
        _status.Text = $"{_doc.Parts.Count} components   {errors} error(s), {warnings} warning(s), {_result.Walkable.Count} walkable   history {_doc.Cursor + 1}/{_doc.HistoryCount}";
        UpdateSelectionUi();
    }

    private void SelectFromParts()
    {
        _doc.Selection.Clear();
        foreach (int i in _partsList.GetSelectedItems())
        {
            if (_partsList.GetItemMetadata(i).VariantType == Variant.Type.Int)
            {
                _doc.Selection.Add((int)_partsList.GetItemMetadata(i));
            }
        }

        UpdateSelectionUi();
    }

    private void OnProblem(long index)
    {
        Variant meta = _problemsList.GetItemMetadata((int)index);
        int m = (int)meta;
        if (m >= 0)
        {
            _doc.Selection.Clear();
            _doc.Selection.Add(m);
            UpdateSelectionUi();
        }
    }

    // --- opening, saving ---------------------------------------------------------------------------------

    public void NewMulti()
    {
        _doc.Reset(Array.Empty<MultiPart>(), "new");
        _doc.Name = "new_multi";
        _doc.Source = null;
        _name.Text = "new_multi";
        _canvas.SetEditZ(Stories.FloorZ);
        _canvas.FitView();
        ValidateNow();
        MarkSaved();
    }

    private void OpenTyped()
    {
        string t = _openId.Text.Trim();
        int id;
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id)
            : int.TryParse(t, out id);
        if (!ok)
        {
            _status.Text = $"'{t}' is not a client multi";
            return;
        }

        bool opened = false;
        GuardUnsaved(() => opened = OpenClientMulti(id));
        if (!opened && !UnsavedPromptOpen)
        {
            _status.Text = $"'{t}' is not a client multi";
        }
    }

    /// <summary>Opens a multi the loaders know (the install's, or one written to a stage this session).</summary>
    public bool OpenClientMulti(int id)
    {
        if (_data?.IsLoaded != true || id < 0)
        {
            return false;
        }

        List<MultiInfo> infos;
        try
        {
            infos = _data.Files.Multis.GetMultis((uint)id);
        }
        catch (Exception)
        {
            return false;
        }

        if (infos.Count == 0)
        {
            return false;
        }

        OpenParts($"multi_{id:X4}", infos.Select(i => new MultiPart { Id = i.ID, X = i.X, Y = i.Y, Z = i.Z, Shown = i.IsVisible }), id);
        _openId.Text = $"0x{id:X4}";
        return true;
    }

    public void OpenParts(string name, IEnumerable<MultiPart> parts, int? source = null)
    {
        _doc.Reset(parts, $"open {name}");
        _doc.Name = name;
        _doc.Source = source;
        _name.Text = name;
        _canvas.SetEditZ(Stories.FloorZ);
        _canvas.FitView();
        ValidateNow();
        MarkSaved();
    }

    public bool OpenDescription(string path)
    {
        try
        {
            (string name, int? source, List<MultiPart> parts) = MultiStore.FromJson(File.ReadAllText(path));
            OpenParts(name, parts, source);
            MarkSaved(path);
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = $"cannot open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    private void OpenDescriptionDialog()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Open a multi description",
            CurrentDir = Directory.Exists(MultiStore.EditDir) ? MultiStore.EditDir : EditorData.RepoRoot,
        };
        dialog.AddFilter("*.multi.json", "Multi description");
        dialog.FileSelected += p =>
        {
            GuardUnsaved(() => OpenDescription(p));
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(800, 500));
    }

    public string SaveDescription()
    {
        string name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _doc.Name;
        string path = MultiStore.SaveDescription(name, _doc.Source, _doc.Parts);
        _saveLog.Text = $"saved {path}";
        _doc.Name = name;
        MarkSaved(path);
        return path;
    }

    private Task<SaveResult> _saving;
    public Task<SaveResult> Saving => _saving;

    /// <summary>
    /// Writes the document into the stage and reads it back. Refused while there are errors. On success the
    /// multi is overlaid on the loaders, so the Multis panel and the World tab can draw it.
    /// </summary>
    public async Task<SaveResult> SaveToStageAsync()
    {
        ValidateNow();
        if (_result.HasErrors)
        {
            var refused = new SaveResult { Error = "fix the errors first: " + string.Join("; ", _result.Findings.Where(f => f.Severity == FindingSeverity.Error).Take(3).Select(f => f.Message)) };
            _saveLog.Text = refused.Error;
            return refused;
        }

        SaveDescription();
        _saveLog.Text = "writing...";
        _saving = MultiStore.SaveAsync(_name.Text, _doc.Parts, _data.Files.TileData.StaticData, StageDir);
        SaveResult r = await _saving;
        if (r.Ok && r.Id is int id)
        {
            Overlay(id, _doc.Parts.Select(p => new MultiInfo { ID = p.Id, X = p.X, Y = p.Y, Z = p.Z, IsVisible = p.Shown }).ToList());
            _saveLog.Text = $"{r.Name} = multi 0x{id:X4}: {r.Components} components, read back equal";
            _lastWritten = id;
            AfterWrite?.Invoke(r);
        }
        else
        {
            _saveLog.Text = r.Error ?? "write failed";
        }

        return r;
    }

    private int? _lastWritten;

    private static void Overlay(int id, List<MultiInfo> infos)
    {
        MultiLoader.EditorOverlay ??= new Dictionary<uint, List<MultiInfo>>();
        MultiLoader.EditorOverlay[(uint)id] = infos;
    }

    /// <summary>Lays every multi of the stage over the loaders, so earlier sessions' work shows in the Multis panel.</summary>
    private void LoadStagedOverlay()
    {
        try
        {
            foreach (var (name, id) in MultiStore.Staged(StageDir))
            {
                List<MultiPart> parts = MultiStore.ReadStaged(StageDir, id);
                if (parts != null)
                {
                    Overlay(id, parts.Select(p => new MultiInfo { ID = p.Id, X = p.X, Y = p.Y, Z = p.Z, IsVisible = p.Shown }).ToList());
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] multi editor stage overlay: {ex.Message}");
        }
    }

    public void PreviewNow()
    {
        if (_lastWritten is int id)
        {
            PreviewInWorld?.Invoke(id);
        }
        else
        {
            _status.Text = "write the multi to the stage first";
        }
    }

    // --- the generator seam -----------------------------------------------------------------------------------

    public void PushComponents(string name, IReadOnlyList<GeneratedPart> parts, bool replace) =>
        PushComponentsWith(name, name, parts, replace, null);

    /// <summary>
    /// <see cref="PushComponents"/> with a second edit in the same undo step (the stair tool opens the floor above).
    /// <paramref name="step"/> names the history entry, <paramref name="name"/> the multi when it replaces.
    /// </summary>
    internal void PushComponentsWith(string step, string name, IReadOnlyList<GeneratedPart> parts, bool replace, Action<List<MultiPart>> extra)
    {
        var made = parts.Select(g => _doc.Make(g.Id, g.X, g.Y, g.Z, g.Shown, g.Hue)).ToList();
        _doc.Do($"{(replace ? "generate" : "add")} {step} ({made.Count})", list =>
        {
            if (replace)
            {
                list.Clear();
            }

            extra?.Invoke(list);
            list.AddRange(made);
        });
        if (replace)
        {
            _doc.Name = name;
            _name.Text = name;
        }

        _canvas.FitView();
    }

    public void Shutdown()
    {
        SetProcess(false);
        if (_data != null)
        {
            _data.Loaded -= OnDataLoaded;
            _data.AssetsApplied -= OnAssetsApplied;
        }

        if (_doc != null)
        {
            _doc.Changed -= OnDocChanged;
        }

        _canvas?.Detach();
        DisposeGenerator();
        AfterWrite = null;
        PreviewInWorld = null;
        MultiLoader.EditorOverlay = null;
    }
}
#endif
