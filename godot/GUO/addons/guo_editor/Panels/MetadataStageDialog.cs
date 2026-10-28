#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>One item, explicit native comparison and independently verified staged promotion.</summary>
[Tool]
public partial class MetadataStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private ItemMetadataEditor _editor;
    private EditorData _data;
    private ItemMetadataNativeReview _review;
    private string _plan;
    private bool _busy;
    private VBoxContainer _comparison;
    private Label _status;
    private Button _recover;
    internal LineEdit Destination { get; private set; }
    internal bool Prepared => _plan != null && !Applied;
    internal bool Applied { get; private set; }
    internal bool Busy => _busy;
    internal string Status => _status.Text;
    internal Task Operation { get; private set; } = Task.CompletedTask;

    internal static MetadataStageDialog Open(ItemMetadataEditor editor, EditorData data, int id)
    {
        ItemMetadataNativeReview review = editor.CaptureNativeReview(data, id);
        var dialog = new MetadataStageDialog(); dialog.Build(editor, data, review); editor.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(740, 430) * EditorInterface.Singleton.GetEditorScale()), 0.9f); return dialog;
    }
    private void Build(ItemMetadataEditor editor, EditorData data, ItemMetadataNativeReview review)
    {
        _editor = editor; _data = data; _review = review;
        Title = $"Export verified item metadata · 0x{review.Document.Id:X4}"; DialogHideOnOk = false;
        OkButtonText = "Prepare verified copy"; CancelButtonText = "Close";
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); AddChild(body);
        body.AddChild(new Label { Text = "Staged data folder" });
        Destination = new LineEdit { Text = Path.Combine(review.Root, "staged-metadata"), AccessibilityName = "Item metadata stage destination" }; body.AddChild(Destination);
        _comparison = new VBoxContainer(); body.AddChild(_comparison); ShowComparison(null, review.Document.PreviewRecord());
        _status = AssetUi.Label("Prepare reads the destination and verifies a private candidate. Apply changes the staged copy; the installation stays read-only."); body.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", TooltipText = "Restore the previous stage and retain the candidate separately" }; body.AddChild(_recover);
        Destination.TextChanged += _ => { if (_busy) return; _plan = null; Applied = false; ShowComparison(null, _review.Document.PreviewRecord()); SetBusy(false, "Destination changed. Prepare a fresh verified copy."); };
        _recover.Pressed += () => Confirm("Recover previous metadata stage?", "Restore the previous stage? The current candidate will be retained separately.", () => Operation = RecoverAsync());
        Confirmed += () =>
        {
            if (_busy || Applied) return;
            if (!Prepared) { Operation = PrepareAsync(); return; }
            Confirm("Apply verified item metadata?", $"Replace the reviewed name, flags and height for item 0x{_review.Document.Id:X4} in the staged tiledata? Other staged fields stay unchanged.", () => Operation = ApplyAsync());
        };
        Canceled += QueueFree; CloseRequested += QueueFree; SetBusy(false);
    }
    private void Confirm(string title, string text, Action action)
    {
        var dialog = new ConfirmationDialog { Title = title, DialogText = text, OkButtonText = title.StartsWith("Recover") ? "Recover previous stage" : "Apply to stage" };
        AddChild(dialog); dialog.Canceled += dialog.QueueFree; dialog.Confirmed += () => { dialog.QueueFree(); action(); }; dialog.PopupCenteredClamped(new Vector2I(620, 240), 0.9f);
    }
    private void Guard() => _editor.RequireNativeReview(_data, _review);
    private string Stage()
    {
        string stage = EditorPaths.OutsideInstall(Destination.Text, _data.ClientData);
        if (_data.IsSelectedInputStage(stage)) throw new IOException("This folder supplies the editor's active data. Choose another stage destination or reload against the installation before changing it.");
        return stage;
    }
    private void ShowComparison(byte[] before, byte[] after)
    {
        foreach (Node child in _comparison.GetChildren()) { _comparison.RemoveChild(child); child.QueueFree(); }
        var grid = new GridContainer { Columns = before == null ? 2 : 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _comparison.AddChild(grid);
        grid.AddChild(new Label { Text = "Field" });
        if (before != null) grid.AddChild(new Label { Text = "Current stage" });
        grid.AddChild(new Label { Text = "Native export" });
        var current = before == null ? default : new ItemMetadataDocument(_review.Document.Id, before).Current;
        var next = new ItemMetadataDocument(_review.Document.Id, after).Current;
        void Row(string label, string oldValue, string newValue)
        {
            grid.AddChild(new Label { Text = label });
            void Value(string text, string side) => grid.AddChild(new LineEdit { Text = text, Editable = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = side + " " + label });
            if (before != null) Value(oldValue, "Current stage"); Value(newValue, "Native export");
        }
        Row("Name", current.Name, next.Name); Row("Flags", $"0x{current.Flags:X16}", $"0x{next.Flags:X16}"); Row("Height", current.Height.ToString(), next.Height.ToString());
        _comparison.AddChild(AssetUi.Label("Other staged fields and native group headers are preserved. Unchanged names retain their original padding."));
    }
    internal async Task PrepareAsync()
    {
        if (_busy) return;
        try
        {
            Guard(); byte[] bytes = _review.Document.NativeStageInput(_review.Root, _review.Install, _review.Source, _review.Token);
            string stage = Stage(), parent = Path.GetDirectoryName(stage), id = Guid.NewGuid().ToString("N");
            string input = EditorPaths.OutsideInstall(Path.Combine(parent, ".fiddler-inputs", id + ".json"), _data.ClientData);
            Directory.CreateDirectory(Path.GetDirectoryName(input)); HueText.Write(input, _data.ClientData, bytes, Guard);
            string plan = Path.Combine(parent, ".fiddler-plans", id);
            SetBusy(true, "Preparing and independently reading the native metadata candidate…");
            JsonNode result = await StaticStageDialog.Run(new[] { "prepare", "--source", _data.ClientData, "--stage", stage,
                "--edits", input, "--plan", plan, "--client-version", _data.ClientVersion }, _lifetime.Token, "metadata.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Guard(); JsonNode row = result["records"][0];
            ShowComparison(Convert.FromBase64String((string)row["before_base64"]), Convert.FromBase64String((string)row["after_base64"]));
            _plan = plan; Applied = false; SetBusy(false, "Verified native metadata. Review the current and exported fields above, then Apply to the staged folder.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task ApplyAsync()
    {
        if (_busy || !Prepared) return;
        try
        {
            Guard(); Stage(); SetBusy(true, "Rechecking and applying the native metadata candidate…");
            await StaticStageDialog.Run(new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" }, _lifetime.Token, "metadata.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Applied = true; SetBusy(false, "Applied and independently verified. The installation is unchanged; the previous stage can be recovered.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task RecoverAsync()
    {
        if (_busy) return;
        try
        {
            string stage = Stage(); SetBusy(true, "Recovering the previous stage…");
            await StaticStageDialog.Run(new[] { "recover", "--stage", stage, "--confirm" }, _lifetime.Token, "metadata.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _plan = null; Applied = false; ShowComparison(null, _review.Document.PreviewRecord()); SetBusy(false, "Previous stage recovered. Prepare again for a fresh review.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    private void Fail(Exception error)
    {
        if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
        _plan = null; SetBusy(false, error.Message);
    }
    private void SetBusy(bool busy, string message = null)
    {
        _busy = busy; Destination.Editable = !busy; GetOkButton().Disabled = busy || Applied;
        OkButtonText = Applied ? "Applied" : Prepared ? "Apply verified metadata" : "Prepare verified copy";
        if (message != null) _status.Text = message;
        string journal = Destination.Text + ".fiddler-journal.json"; bool recoverable = File.Exists(journal);
        if (recoverable)
            try { recoverable = (string)JsonNode.Parse(HueText.Read(journal))?["state"] != "recovered"; }
            catch { }
        _recover.Visible = recoverable; _recover.Disabled = busy;
    }
    public override void _ExitTree() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
