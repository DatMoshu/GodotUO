#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;

/// <summary>Review explicit effect scope, encoding and filenames before an incremental export.</summary>
[Tool]
public partial class SoundExportDialog : ConfirmationDialog
{
    private SoundPanel _panel;
    private int[][] _scopes;
    private OptionButton _scope, _format;
    private CheckBox _prefix, _pol;
    private LineEdit _destination;
    private Button _browse;
    private Tree _rows;
    private Label _status, _count;
    private ProgressBar _progress;
    private SoundExportPlan _plan;
    private SoundExportJob _job;
    private bool _reviewError;
    private int _browserTotal, _selectedTotal;
    internal bool IsBusy => _job is { Finished: false };
    internal SoundExportPlan Plan => _plan;
    internal Tree Rows => _rows;
    internal LineEdit Destination => _destination;
    internal string Status => _status.Text;
    internal bool ExportSucceeded => _job?.Succeeded == true;
    private SoundExportFormat Format => (SoundExportFormat)_format.Selected;

    internal static SoundExportDialog Open(SoundPanel panel, int[] all, int[] browser, int[] selected, bool wave, int browserTotal, int selectedTotal)
    {
        var dialog = new SoundExportDialog { _panel = panel, _scopes = new[] { all, browser, selected }, _browserTotal = browserTotal, _selectedTotal = selectedTotal,
            Title = "Export sound effects", DialogHideOnOk = false, OkButtonText = "Export" };
        dialog.Build(); panel.AddChild(dialog);
        dialog._format.Selected = wave ? 2 : 0; dialog._scope.Selected = wave ? 2 : 0; dialog.Review(); dialog.FitReview(); return dialog;
    }
    private void Build()
    {
        var body = new VBoxContainer { Size = new Vector2(840, 0) * EditorInterface.Singleton.GetEditorScale() }; AddChild(body);
        var options = new HBoxContainer(); body.AddChild(options);
        options.AddChild(new Label { Text = "Scope" }); _scope = new OptionButton { AccessibilityName = "Sound export scope" };
        foreach (string label in new[] { $"All occupied effects ({_scopes[0].Length})", $"Browser occupied ({_scopes[1].Length}/{_browserTotal})", $"Selected occupied ({_scopes[2].Length}/{_selectedTotal})" }) _scope.AddItem(label);
        options.AddChild(_scope); options.AddChild(new Label { Text = "Format" });
        _format = new OptionButton { AccessibilityName = "Sound export format", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string label in new[] { "CSV · UTF-8", "CSV · Fiddler Windows-1252", "WAV batch · new folder" }) _format.AddItem(label);
        options.AddChild(_format);
        var flags = new HBoxContainer(); body.AddChild(flags);
        _prefix = new CheckBox { Text = "Include native ID in WAV filenames", ButtonPressed = true }; flags.AddChild(_prefix);
        _pol = new CheckBox { Text = "POL IDs (+1) · CSV only" }; flags.AddChild(_pol);
        _count = AssetUi.Label(""); _count.AutowrapMode = TextServer.AutowrapMode.Off; body.AddChild(_count);
        _rows = new Tree { Columns = 4, ColumnTitlesVisible = true, HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 230) * EditorInterface.Singleton.GetEditorScale() };
        foreach (var pair in new[] { (0, "Native ID"), (1, "Working name"), (2, "Seconds"), (3, "Output") }) _rows.SetColumnTitle(pair.Item1, pair.Item2);
        _rows.SetColumnExpand(0, false); _rows.SetColumnCustomMinimumWidth(0, (int)(90 * EditorInterface.Singleton.GetEditorScale())); body.AddChild(_rows);
        body.AddChild(AssetUi.Label("Empty slots are excluded from every scope. CSV uses ID;Name;Length. WAVs use reviewed working PCM and include a UTF-8 SoundList.csv. Project names and unsaved PCM are included; native archives stay unchanged."));
        var destination = new HBoxContainer(); body.AddChild(destination);
        _destination = new LineEdit { PlaceholderText = "Choose a CSV file or a new WAV output folder", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Sound export destination" }; destination.AddChild(_destination);
        _browse = AssetUi.Action("Folder", "Choose export destination…", () => Browse()); destination.AddChild(_browse);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true }; body.AddChild(_progress);
        _status = AssetUi.Label(""); body.AddChild(_status);
        _scope.ItemSelected += _ => Review(); _format.ItemSelected += _ => { _destination.Text = ""; Review(); };
        _prefix.Toggled += _ => DrawRows(); _pol.Toggled += _ => DrawRows();
        _destination.TextChanged += _ => State();
        Confirmed += StartExport; Canceled += () => { _job?.Cancel(); QueueFree(); };
    }
    internal void Choose(int scope, SoundExportFormat format, bool prefix = true, bool pol = false)
    { _scope.Selected = scope; _format.Selected = (int)format; _prefix.ButtonPressed = prefix; _pol.ButtonPressed = pol; Review(); }
    private void Review()
    {
        if (IsBusy) return;
        _job?.Dispose(); _job = null;
        GetCancelButton().Text = "Cancel";
        _prefix.Visible = Format == SoundExportFormat.WaveFolder; _pol.Visible = Format != SoundExportFormat.WaveFolder;
        OkButtonText = Format == SoundExportFormat.WaveFolder ? "Export WAV folder" : "Export CSV";
        try
        {
            _plan = _panel.ExportPlan(_scopes[_scope.Selected]);
            _rows.CustomMinimumSize = new Vector2(0, Math.Clamp(_plan.Rows.Count, 3, 10) * 26) * EditorInterface.Singleton.GetEditorScale();
            DrawRows();
        }
        catch (Exception e) { _plan = null; _rows.Clear(); _count.Text = "Choose a scope containing readable effects."; _status.Text = e.Message; State(); }
        // Refit once after wrapped labels have their real width; early minimum-size estimates can be much taller.
        Callable.From(() => { if (IsInstanceValid(this) && IsInsideTree() && Visible) FitReview(); }).CallDeferred();
    }
    private void FitReview() => PopupCenteredClamped((Vector2I)(new Vector2(880, 360 + Math.Clamp(_plan?.Rows.Count ?? 3, 3, 10) * 26)
        * EditorInterface.Singleton.GetEditorScale()), 0.9f);
    private void DrawRows()
    {
        if (_plan == null) return;
        _reviewError = false;
        _rows.Clear(); TreeItem root = _rows.CreateItem();
        try
        {
            string[] names = Format == SoundExportFormat.WaveFolder ? _plan.Rows.Select(row => SoundExportPlan.FileName(row.Id, row.Name, _prefix.ButtonPressed)).ToArray() : null;
            foreach (var pair in _plan.Rows.Take(250).Select((row, index) => (row, index)))
            {
                TreeItem item = _rows.CreateItem(root); item.SetText(0, $"0x{pair.row.Id:X4}"); item.SetText(1, pair.row.Name);
                item.SetText(2, (pair.row.Samples / (double)SoundWave.Frequency).ToString("0.000"));
                item.SetText(3, names == null ? $"CSV ID 0x{pair.row.Id + (_pol.ButtonPressed ? 1 : 0):X3}" : names[pair.index]);
                for (int column = 0; column < 4; column++) item.SetTooltipText(column, item.GetText(column));
            }
            _count.Text = $"{_plan.Rows.Count} effects · showing first {Math.Min(250, _plan.Rows.Count)} · scope captured when opened";
            if (names != null) _plan.FileNames(_prefix.ButtonPressed);
            _plan.Csv(Format == SoundExportFormat.FiddlerCsv, _pol.ButtonPressed && Format != SoundExportFormat.WaveFolder ? 1 : 0);
            _status.Text = Format == SoundExportFormat.FiddlerCsv ? "Windows-1252 · current locale decimal separator · unrepresentable names are refused." : "Ready for an explicit destination. No output written.";
        }
        catch (Exception e) { _status.Text = e.Message; _reviewError = true; }
        State();
    }
    private void State()
    { GetOkButton().Disabled = IsBusy || _plan == null || _reviewError || string.IsNullOrWhiteSpace(_destination.Text); }
    internal EditorFileDialog Browse()
    {
        bool wave = Format == SoundExportFormat.WaveFolder;
        var dialog = new EditorFileDialog { FileMode = wave ? EditorFileDialog.FileModeEnum.OpenDir : EditorFileDialog.FileModeEnum.SaveFile,
            Access = EditorFileDialog.AccessEnum.Filesystem, Title = wave ? "Choose parent folder for a new sound export" : "Export SoundList.csv" };
        if (!wave) { dialog.Filters = new[] { "*.csv ; Sound list CSV" }; dialog.CurrentFile = "SoundList.csv"; }
        dialog.FileSelected += path => { _destination.Text = path; State(); dialog.QueueFree(); };
        dialog.DirSelected += path => { _destination.Text = Path.Combine(path, "sounds-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); State(); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree; AddChild(dialog); dialog.PopupFileDialog(); return dialog;
    }
    private void StartExport()
    {
        if (IsBusy || _plan == null || _reviewError) return;
        try
        {
            _job?.Dispose(); _job = null; _job = new(_plan, Format, _destination.Text, _prefix.ButtonPressed, _pol.ButtonPressed ? 1 : 0);
            SetBusy(true); _status.Text = "Exporting reviewed effects… Cancel leaves completed exports unchanged.";
        }
        catch (Exception e) { _status.Text = e.Message; State(); }
    }
    private void SetBusy(bool busy)
    {
        _scope.Disabled = _format.Disabled = _prefix.Disabled = _pol.Disabled = _browse.Disabled = busy;
        _destination.Editable = !busy; State();
    }
    public override void _Process(double delta)
    {
        if (!IsBusy) return;
        ulong start = Time.GetTicksMsec();
        for (int count = 0; count < 8 && IsBusy && Time.GetTicksMsec() - start < 8; count++) _job.Step();
        _progress.Value = 100.0 * _job.CompletedRows / _plan.Rows.Count;
        _status.Text = $"Exported {_job.CompletedRows} / {_plan.Rows.Count} effects…";
        if (_job.Finished)
        {
            _status.Text = _job.Succeeded ? $"Export complete · {_plan.Rows.Count} effects · {_destination.Text}" : _job.Error;
            GetCancelButton().Text = _job.Succeeded ? "Close" : "Cancel";
            SetBusy(false);
        }
    }
    public override void _ExitTree() => _job?.Dispose();
}
#endif
