#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Review every changed native string, then separately confirm staged promotion.</summary>
[Tool]
public partial class ClilocStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private ClilocPanel _panel;
    private ClilocDocument _document;
    private EditorData _data;
    private string _token, _plan;
    private bool _busy;
    private int _page;
    private JsonArray _records;
    private JsonNode _counts;
    private Tree _rows;
    private TextEdit _before, _after;
    private Label _summary, _status, _pages;
    private Button _recover, _previous, _next;
    internal LineEdit Destination { get; private set; }
    internal LineEdit Filename { get; private set; }
    internal bool Prepared => _plan != null && !Applied;
    internal bool Applied { get; private set; }
    internal bool Busy => _busy;
    internal string Status => _status.Text;
    internal Task Operation { get; private set; } = Task.CompletedTask;
    internal static ClilocStageDialog Open(ClilocPanel panel, ClilocDocument document, EditorData data)
    {
        panel.RequireNativeReview(document, document.NativeReviewToken);
        var dialog = new ClilocStageDialog(); dialog.Build(panel, document, data); panel.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(900, 610) * EditorInterface.Singleton.GetEditorScale()), 0.9f); return dialog;
    }
    private void Build(ClilocPanel panel, ClilocDocument document, EditorData data)
    {
        _panel = panel; _document = document; _data = data; _token = document.NativeReviewToken; panel.RequireNativeReview(document, _token);
        Title = "Export verified native Cliloc · " + document.DisplayName; DialogHideOnOk = false; CancelButtonText = "Close";
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 8); AddChild(body);
        body.AddChild(AssetUi.Label("Staged data folder")); Destination = new LineEdit { Text = Path.Combine(document.Project, "staged-cliloc"), AccessibilityName = "Native Cliloc stage destination" }; body.AddChild(Destination);
        var fileRow = new HBoxContainer(); body.AddChild(fileRow); fileRow.AddChild(new Label { Text = "Native filename" });
        Filename = new LineEdit { Text = document.IsCustom ? "Cliloc." + document.Language : Path.GetFileName(document.SourceFile), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Native Cliloc output filename" }; fileRow.AddChild(Filename);
        body.AddChild(AssetUi.Label("Source: " + document.DisplayName + (document.Compressed ? " · BWT compressed" : " · plain UTF-8") + ". This export does not register or activate a runtime language."));
        _summary = AssetUi.Label("Prepare to compare the current native records with this source document's project changes."); body.AddChild(_summary);
        _rows = new Tree { Columns = 4, HideRoot = true, ColumnTitlesVisible = true, CustomMinimumSize = new Vector2(0, 110) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = "Native Cliloc reviewed changes" }; body.AddChild(_rows);
        foreach (var column in new[] { (0, "ID"), (1, "Action"), (2, "Current stage"), (3, "Native export") }) _rows.SetColumnTitle(column.Item1, column.Item2);
        _rows.SetColumnExpand(0, false); _rows.SetColumnCustomMinimumWidth(0, (int)(90 * EditorInterface.Singleton.GetEditorScale()));
        _rows.SetColumnExpand(1, false); _rows.SetColumnCustomMinimumWidth(1, (int)(75 * EditorInterface.Singleton.GetEditorScale()));
        var pager = new HBoxContainer(); body.AddChild(pager);
        _previous = AssetUi.Action("Back", "Previous 100 native Cliloc changes", () => { _page--; FillRows(); }); pager.AddChild(_previous);
        _pages = new Label(); pager.AddChild(_pages); _next = AssetUi.Action("Forward", "Next 100 native Cliloc changes", () => { _page++; FillRows(); }); pager.AddChild(_next);
        var detail = new HBoxContainer(); body.AddChild(detail);
        TextEdit Text(string name) { var edit = new TextEdit { Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 100) * EditorInterface.Singleton.GetEditorScale(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = name }; detail.AddChild(edit); return edit; }
        _before = Text("Current native Cliloc full text"); _after = Text("Exported native Cliloc full text");
        _rows.ItemSelected += ShowSelected;
        _status = AssetUi.Label("Prepare verifies a private candidate. Apply changes the staged copy; the source files remain read-only."); body.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", TooltipText = "Restore the previous stage and retain this candidate separately" }; body.AddChild(_recover);
        Destination.TextChanged += _ => Invalidate(); Filename.TextChanged += _ => Invalidate();
        _recover.Pressed += () => Confirm("Recover previous Cliloc stage?", "Restore the previous stage and retain the current candidate separately?", () => Operation = RecoverAsync());
        Confirmed += () =>
        {
            if (_busy || Applied) return;
            if (!Prepared) { Operation = PrepareAsync(); return; }
            int removed = _counts?["removed_ids"]?.AsArray().Count ?? 0, added = _counts?["added_ids"]?.AsArray().Count ?? 0;
            Confirm("Apply verified native Cliloc?", $"Apply the reviewed changes to {Filename.Text} in the staged folder? {added:N0} IDs added; {removed:N0} IDs removed (all duplicate occurrences of removed IDs). Untouched staged records and other files are preserved.", () => Operation = ApplyAsync());
        };
        Canceled += QueueFree; CloseRequested += QueueFree; FillRows(); SetBusy(false);
    }
    private void Confirm(string title, string text, Action action)
    {
        var dialog = new ConfirmationDialog { Title = title, DialogText = text, DialogAutowrap = true, OkButtonText = title.StartsWith("Recover") ? "Recover previous stage" : "Apply to stage" };
        AddChild(dialog); dialog.Canceled += dialog.QueueFree; dialog.Confirmed += () => { dialog.QueueFree(); action(); };
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(700, 240) * EditorInterface.Singleton.GetEditorScale()), 0.9f);
    }
    private void Guard() => _panel.RequireNativeReview(_document, _token);
    private string Stage()
    {
        string path = _document.WritablePath(Destination.Text);
        if (_data.IsSelectedInputStage(path)) throw new IOException("This folder supplies active editor data. Choose another destination or reload against the installation.");
        return path;
    }
    private void Invalidate(string reason = "Destination or filename changed. Prepare a fresh native comparison.")
    {
        if (_busy) return;
        _plan = null; Applied = false; _records = null; _counts = null; _page = 0; FillRows();
        _summary.Text = reason; SetBusy(false);
    }
    private void FillRows()
    {
        _rows.Clear(); TreeItem root = _rows.CreateItem(); int count = _records?.Count ?? 0;
        _page = Math.Clamp(_page, 0, Math.Max(0, (count - 1) / 100));
        for (int i = _page * 100; i < Math.Min(count, (_page + 1) * 100); i++)
        {
            JsonNode row = _records[i]; TreeItem item = _rows.CreateItem(root); item.SetMetadata(0, i);
            string before = (string)row["before"], after = (string)row["after"];
            item.SetText(0, ((int)row["id"]).ToString()); item.SetText(1, after == null ? "Remove" : before == null ? "Add" : before == after ? "Unchanged" : "Edit");
            item.SetText(2, (before ?? "[absent]").Replace('\r', ' ').Replace('\n', ' ')); item.SetText(3, (after ?? "[removed]").Replace('\r', ' ').Replace('\n', ' '));
        }
        _pages.Text = count == 0 ? "No reviewed changes" : $"{count:N0} reviewed {(count == 1 ? "record" : "records")} · page {_page + 1}/{(count + 99) / 100}";
        _previous.Disabled = _page == 0; _next.Disabled = (_page + 1) * 100 >= count;
        _before.Text = ""; _after.Text = ""; if (root.GetFirstChild() is { } first) { first.Select(0); ShowSelected(); }
    }
    private void ShowSelected()
    {
        if (_rows.GetSelected() is not { } item || _records == null) return;
        JsonNode row = _records[item.GetMetadata(0).AsInt32()]; _before.Text = (string)row["before"] ?? "[absent]"; _after.Text = (string)row["after"] ?? "[removed]";
    }
    internal async Task PrepareAsync()
    {
        if (_busy) return;
        try
        {
            Guard(); byte[] bytes = _document.NativeStageInput(_token); string stage = Stage(), id = Guid.NewGuid().ToString("N");
            string parent = Path.GetDirectoryName(stage), input = _document.WritablePath(Path.Combine(parent, ".fiddler-inputs", id + ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(input)); HueText.Write(input, _document.Install, bytes, Guard);
            string plan = Path.Combine(parent, ".fiddler-plans", id); SetBusy(true, "Preparing and independently reading native Cliloc records…");
            JsonNode result = await StaticStageDialog.Run(new[] { "prepare", "--source", _document.Install, "--input-file", _document.SourceFile,
                "--stage", stage, "--filename", Filename.Text, "--edits", input, "--plan", plan, "--client-version", _data.ClientVersion }, _lifetime.Token, "cliloc_stage.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Guard(); _plan = plan; _records = result["records"].AsArray(); _counts = result["counts"]; _page = 0; FillRows();
            _summary.Text = $"{_counts["added_ids"].AsArray().Count:N0} added · {_counts["edited_ids"].AsArray().Count:N0} edited · {_counts["removed_ids"].AsArray().Count:N0} removed IDs · {(int)_counts["preserved_records"]:N0} records preserved · {(string)result["output_encoding"]}";
            SetBusy(false, "Verified. Review the records and full text above, then Apply to the staged folder.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fail(error); }
    }
    internal async Task ApplyAsync()
    {
        if (_busy || !Prepared) return;
        try
        {
            Guard(); Stage(); SetBusy(true, "Rechecking and applying the native Cliloc candidate…");
            await StaticStageDialog.Run(new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" }, _lifetime.Token, "cliloc_stage.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Applied = true; SetBusy(false, "Applied and independently verified. Source files are unchanged; the previous stage can be recovered.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fail(error); }
    }
    internal async Task RecoverAsync()
    {
        if (_busy) return;
        try
        {
            string stage = Stage(); SetBusy(true, "Recovering the previous native Cliloc stage…");
            await StaticStageDialog.Run(new[] { "recover", "--stage", stage, "--confirm" }, _lifetime.Token, "cliloc_stage.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _busy = false; Invalidate("Prepare a fresh native comparison against the recovered stage."); SetBusy(false, "Previous stage recovered. Prepare again for a fresh comparison.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fail(error); }
    }
    private void Fail(Exception error)
    {
        if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
        _busy = false; Invalidate("Native review requires a fresh preparation."); SetBusy(false, error.Message);
    }
    private void SetBusy(bool busy, string message = null)
    {
        _busy = busy; Destination.Editable = Filename.Editable = !busy; GetOkButton().Disabled = busy || Applied;
        OkButtonText = Applied ? "Applied" : Prepared ? "Apply verified Cliloc" : "Prepare verified copy";
        if (message != null) _status.Text = message;
        bool recoverable = File.Exists(Destination.Text + ".fiddler-journal.json");
        if (recoverable) try { recoverable = (string)JsonNode.Parse(HueText.Read(Destination.Text + ".fiddler-journal.json"))?["state"] != "recovered"; } catch { }
        _recover.Visible = recoverable; _recover.Disabled = busy;
    }
    public override void _ExitTree() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
