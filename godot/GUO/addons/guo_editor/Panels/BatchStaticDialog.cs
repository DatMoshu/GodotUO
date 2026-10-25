#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>F03a folder review. One verified candidate, explicit apply, whole-stage recovery.</summary>
[Tool]
public partial class BatchStaticDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private EditorData _data;
    private LineEdit _folder, _destination;
    private Tree _rows;
    private HBoxContainer _comparison;
    private Label _status;
    private Button _scanButton, _browse, _recover;
    private JsonNode _scan;
    private string _plan;
    private bool _busy, _applied;
    internal Tree Rows => _rows;
    internal bool IsBusy => _busy;
    internal bool ReadyToPrepare => _scan != null && (bool?)_scan["ready"] == true && !_busy;

    public static BatchStaticDialog Open(EditorData data)
    {
        var dialog = new BatchStaticDialog(); dialog.Build(data);
        GuoEditorPlugin.AssetsMain.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(1000, 660) * EditorInterface.Singleton.GetEditorScale()), 0.9f);
        return dialog;
    }

    private void Build(EditorData data)
    {
        _data = data; Title = "Batch static replacement";
        DialogHideOnOk = false; OkButtonText = "Prepare verified batch"; CancelButtonText = "Close";
        Canceled += QueueFree; CloseRequested += QueueFree; Confirmed += ConfirmOperation;
        var body = new VBoxContainer(); AddChild(body);
        body.AddChild(AssetUi.Label("Replace occupied static IDs from a PNG folder. Review every ID before preparing; the installed client remains read-only."));
        var paths = new GridContainer { Columns = 2 }; body.AddChild(paths);
        paths.AddChild(new Label { Text = "PNG folder" });
        var input = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; paths.AddChild(input);
        _folder = new LineEdit { PlaceholderText = "26.png, 0x001A.png or static_0x001A.png", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Batch PNG folder" }; input.AddChild(_folder);
        _browse = AssetUi.Action("Folder", "Choose PNG folder", () =>
        {
            var picker = new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenDir, Access = EditorFileDialog.AccessEnum.Filesystem, Title = "Choose batch PNG folder" };
            AddChild(picker); picker.DirSelected += path => { _folder.Text = path; Invalidate(); picker.QueueFree(); };
            picker.Canceled += picker.QueueFree; picker.PopupCenteredRatio(0.65f);
        }); input.AddChild(_browse);
        _scanButton = new Button { Text = "Scan folder" }; _scanButton.Pressed += () => _ = ScanAsync(); input.AddChild(_scanButton);
        paths.AddChild(new Label { Text = "Staged data folder" });
        _destination = new LineEdit { Text = Path.Combine(data.Assets.Root, "staged-static-art"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Batch staged data folder" }; paths.AddChild(_destination);
        _folder.TextChanged += _ => Invalidate(); _destination.TextChanged += _ => Invalidate();
        body.AddChild(AssetUi.Label("Top-level PNGs only · decimal or explicit 0x IDs · at most 256 images / 1,000,000 pixels. Other files are ignored. Metadata documents are not included."));
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) * EditorInterface.Singleton.GetEditorScale() }; body.AddChild(split);
        _rows = new Tree { Columns = 3, ColumnTitlesVisible = true, HideRoot = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Batch replacement preview" };
        _rows.SetColumnTitle(0, "File / ID"); _rows.SetColumnTitle(1, "Size"); _rows.SetColumnTitle(2, "Result");
        split.AddChild(_rows); _rows.ItemSelected += SelectRow;
        _comparison = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; split.AddChild(_comparison);
        _comparison.AddChild(AssetUi.Label("Select a scanned image to compare it with the installed source."));
        _status = AssetUi.Label("Choose a folder and scan it. No staged files have changed."); body.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", Visible = false }; _recover.Pressed += ConfirmRecovery; body.AddChild(_recover);
        Invalidate();
    }

    internal void Invalidate()
    {
        if (_busy) return;
        _scan = null; _plan = null; _applied = false;
        _rows.Clear(); ClearComparison();
        _status.Text = "Scan the folder before preparing. No staged files have changed.";
        OkButtonText = "Prepare verified batch"; GetOkButton().Disabled = true; UpdateRecovery();
    }

    private void ClearComparison()
    { foreach (Node child in _comparison.GetChildren()) { _comparison.RemoveChild(child); child.QueueFree(); } }

    private async Task ScanAsync()
    {
        if (_busy) return;
        _scan = null; _plan = null; _applied = false; _rows.Clear(); ClearComparison();
        SetBusy(true, "Reading filenames, PNGs and occupied IDs. The stage is unchanged…");
        try
        {
            JsonNode result = await StaticStageDialog.Run(new[] { "scan", "--folder", _folder.Text, "--source", _data.ClientData, "--stage", _destination.Text }, _lifetime.Token, "batch.py");
            if (!Alive()) return;
            ShowScan(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Alive()) { _scan = null; _status.Text = e.Message; } }
        finally { if (Alive()) SetBusy(false); }
    }

    internal void ShowScan(JsonNode result)
    {
        if (result["folder"] != null) _folder.Text = (string)result["folder"];
        _scan = result; _rows.Clear(); TreeItem root = _rows.CreateItem();
        foreach (JsonNode row in result["rows"].AsArray())
        {
            TreeItem item = _rows.CreateItem(root); int? id = (int?)row["id"];
            item.SetText(0, $"{row["file"]}" + (id.HasValue ? $" · 0x{id:X4}" : ""));
            item.SetText(1, row["width"] == null ? "—" : $"{row["width"]} × {row["height"]}");
            string error = (string)row["error"] ?? "";
            item.SetText(2, error.Length > 0 ? error : "Replace occupied static"); item.SetTooltipText(2, error);
            item.SetMetadata(0, row.ToJsonString());
        }
        _status.Text = (bool?)result["ready"] == true
            ? $"{result["count"]} replacements ready for review · {result["ignored"]} other entries ignored. Prepare verifies a separate copy; Apply requires confirmation."
            : $"{result["errors"].AsArray().Count} error(s); preparation is blocked. Inspect the Result column for each file.\n"
                + string.Join("\n", result["errors"].AsArray().Take(3));
        GetOkButton().Disabled = !ReadyToPrepare;
        root.GetFirstChild()?.Select(0); SelectRow();
    }

    private void SelectRow()
    {
        TreeItem selected = _rows.GetSelected(); if (selected == null) return;
        ClearComparison();
        try
        {
            JsonNode row = JsonNode.Parse(selected.GetMetadata(0).AsString());
            if (!string.IsNullOrEmpty((string)row["error"])) { _comparison.AddChild(AssetUi.Label((string)row["error"])); return; }
            int id = (int)row["id"];
            StaticStageDialog.AddPreview(_comparison, "Installed source · read-only", _data.SourceArtImage(EditorData.LandCount + (uint)id));
            using Image image = Image.LoadFromFile((string)row["path"]);
            StaticStageDialog.AddPreview(_comparison, "Folder PNG · pre-quantization", image);
        }
        catch (Exception e) { _comparison.AddChild(AssetUi.Label(e.Message)); }
    }

    private void ConfirmOperation()
    {
        if (_busy || _applied || !ReadyToPrepare) return;
        if (_plan == null) { _ = ExecuteAsync(false); return; }
        var confirm = new ConfirmationDialog { Title = "Apply the whole verified batch?", DialogText = $"Replace {_scan["count"]} occupied static IDs in the staged folder? Every input is rechecked. The previous stage is retained for recovery; the installed client is unchanged.", OkButtonText = "Apply batch to stage" };
        AddChild(confirm); confirm.Canceled += confirm.QueueFree;
        confirm.Confirmed += () => { confirm.QueueFree(); _ = ExecuteAsync(true); }; confirm.PopupCentered();
    }

    private async Task ExecuteAsync(bool apply)
    {
        SetBusy(true, apply ? "Rechecking every input and applying the whole batch…" : "Preparing and independently verifying the whole batch…");
        try
        {
            string[] args;
            if (apply) args = new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" };
            else
            {
                string scanFile = Path.Combine(_data.Assets.Root, ".fiddler-inputs", Guid.NewGuid().ToString("N") + ".json");
                AssetActions.EnsureExportDestination(scanFile); Directory.CreateDirectory(Path.GetDirectoryName(scanFile));
                File.WriteAllText(scanFile, _scan.ToJsonString());
                _plan = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_destination.Text)), ".fiddler-plans", Guid.NewGuid().ToString("N"));
                args = new[] { "prepare", "--scan", scanFile, "--plan", _plan, "--client-version", _data.ClientVersion };
            }
            await StaticStageDialog.Run(args, _lifetime.Token, apply ? "replacement.py" : "batch.py");
            if (!Alive()) return;
            _applied = apply; OkButtonText = apply ? "Applied" : "Apply verified batch";
            _status.Text = apply ? "Batch applied and independently verified. Project image overlays are unchanged." : $"All {_scan["count"]} replacements verified. Review the table, then Apply to publish the candidate to the staged folder.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Alive()) { _plan = null; _scan = null; _status.Text = e.Message + "\nScan again before preparing."; OkButtonText = "Prepare verified batch"; } }
        finally { if (Alive()) SetBusy(false); }
    }

    private void ConfirmRecovery()
    {
        if (_busy) return;
        var confirm = new ConfirmationDialog { Title = "Recover previous stage?", DialogText = "Retain the current stage separately and restore the whole prior stage.", OkButtonText = "Recover previous stage" };
        AddChild(confirm); confirm.Canceled += confirm.QueueFree;
        confirm.Confirmed += async () =>
        {
            confirm.QueueFree(); SetBusy(true, "Recovering…");
            try
            {
                await StaticStageDialog.Run(new[] { "recover", "--stage", _destination.Text, "--confirm" }, _lifetime.Token);
                if (Alive()) { SetBusy(false); Invalidate(); _status.Text = "Previous stage recovered. Scan again before preparing another batch."; }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (Alive()) SetBusy(false, e.Message); }
        }; confirm.PopupCentered();
    }

    private bool Alive() => GodotObject.IsInstanceValid(this) && !_lifetime.IsCancellationRequested;
    private void SetBusy(bool busy, string text = null)
    {
        _busy = busy; _folder.Editable = _destination.Editable = !busy;
        _scanButton.Disabled = _browse.Disabled = busy;
        GetOkButton().Disabled = busy || _applied || !ReadyToPrepare;
        if (text != null) _status.Text = text; UpdateRecovery();
    }
    private void UpdateRecovery()
    { _recover.Visible = File.Exists(_destination.Text + ".fiddler-journal.json"); _recover.Disabled = _busy; }
    public override void _ExitTree() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
