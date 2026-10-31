#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Native hue export with a captured document, reviewed candidate and separate apply.</summary>
[Tool]
public partial class HueStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private HuePanel _panel;
    private EditorData _data;
    private HueDocument _document;
    private string _token, _plan;
    private bool _busy;
    private VBoxContainer _comparison;
    private Label _status;
    private Button _recover;
    internal LineEdit Destination { get; private set; }
    internal bool Prepared => _plan != null && !Applied;
    internal bool Applied { get; private set; }
    internal string Status => _status.Text;
    internal bool Busy => _busy;
    internal Task Operation { get; private set; } = Task.CompletedTask;

    internal static HueStageDialog Open(HuePanel panel, EditorData data, HueDocument document)
    {
        var dialog = new HueStageDialog(); dialog.Build(panel, data, document); panel.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(720, 520) * EditorInterface.Singleton.GetEditorScale()), 0.9f);
        return dialog;
    }
    internal void Build(HuePanel panel, EditorData data, HueDocument document)
    {
        _panel = panel; _data = data; _document = document; _token = document.ReviewToken; Guard();
        Title = $"Export verified hue stage · 0x{document.Id:X4}"; DialogHideOnOk = false;
        OkButtonText = "Prepare verified copy"; CancelButtonText = "Close";
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); AddChild(body);
        body.AddChild(AssetUi.Label("Native hue export · installation stays read-only"));
        var projectName = new LineEdit { Text = document.Name, Editable = false, AccessibilityName = "Complete project hue name" }; body.AddChild(projectName);
        if (document.NameWarning.Length > 0) body.AddChild(AssetUi.Label(document.NameWarning));
        body.AddChild(new Label { Text = "Staged data folder" });
        Destination = new LineEdit { Text = Path.Combine(document.Project, "staged-hues"), AccessibilityName = "Hue stage destination" }; body.AddChild(Destination);
        Destination.TextChanged += _ => { if (_busy) return; _plan = null; Applied = false; SetBusy(false, "Destination changed. Prepare a fresh verified copy."); };
        _comparison = new VBoxContainer(); body.AddChild(_comparison);
        ShowComparison(document.PreviewRecord(), document.PreviewRecord(), prepared: false);
        _status = AssetUi.Label("Prepare checks the source and creates an independently verified candidate. The destination changes only after Apply."); body.AddChild(_status);
        body.AddChild(AssetUi.Label("The previous stage is retained for recovery. An interrupted apply may need recovery; closing does not undo a completed apply."));
        _recover = new Button { Text = "Recover previous stage…", TooltipText = "Restore the previous stage and retain the current candidate separately" }; body.AddChild(_recover);
        _recover.Pressed += () => Confirm("Recover previous hue stage?", "Restore the previous stage? The current candidate will be retained separately.", () => Operation = RecoverAsync());
        Confirmed += () =>
        {
            if (_busy || Applied) return;
            if (!Prepared) { Operation = PrepareAsync(); return; }
            Confirm("Apply verified hue to stage?", $"Replace hue 0x{_document.Id:X4} in the reviewed staged file? The installation stays unchanged.", () => Operation = ApplyAsync());
        };
        Canceled += QueueFree; CloseRequested += QueueFree; SetBusy(false);
    }
    private void Confirm(string title, string text, Action action)
    {
        var confirmation = new ConfirmationDialog { Title = title, DialogText = text, OkButtonText = title.StartsWith("Recover") ? "Recover previous stage" : "Apply to stage" };
        AddChild(confirmation); confirmation.Canceled += confirmation.QueueFree;
        confirmation.Confirmed += () => { confirmation.QueueFree(); action(); }; confirmation.PopupCentered();
    }
    private void Guard()
    {
        if (!_panel.IsCurrentProject(_document)) throw new IOException("The world project changed. Reopen native export in the current project.");
        _document.RequireNativeReview(_token);
    }
    private void ShowComparison(byte[] before, byte[] after, bool prepared)
    {
        foreach (Node child in _comparison.GetChildren()) { _comparison.RemoveChild(child); child.QueueFree(); }
        _comparison.AddChild(AssetUi.Label(prepared ? "Verified staged change" : "Working palette · prepare to read the destination"));
        var fields = new GridContainer { Columns = 2 }; _comparison.AddChild(fields);
        void Row(string label, byte[] record)
        {
            fields.AddChild(new Label { Text = label });
            int nul = Array.IndexOf(record, (byte)0, 68, 20);
            fields.AddChild(new LineEdit { Text = Encoding.ASCII.GetString(record, 68, nul < 0 ? 20 : nul - 68), Editable = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = label + " native name" });
            fields.AddChild(new Label { Text = $"0x{BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(64)):X4}–0x{BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(66)):X4}" });
            fields.AddChild(HueTextDialog.Strip(Enumerable.Range(0, 32).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i * 2))).ToArray()));
        }
        if (prepared) Row("Current staged hue", before);
        Row("Native export", after);
    }
    internal async Task PrepareAsync()
    {
        if (_busy) return;
        try
        {
            Guard(); byte[] bytes = _document.NativeStageInput(_token);
            string stage = EditorPaths.OutsideInstall(Destination.Text, _data.ClientData);
            string parent = Path.GetDirectoryName(stage), id = Guid.NewGuid().ToString("N");
            string input = EditorPaths.OutsideInstall(Path.Combine(parent, ".fiddler-inputs", id + ".json"), _data.ClientData);
            Directory.CreateDirectory(Path.GetDirectoryName(input)); HueText.Write(input, _data.ClientData, bytes, Guard);
            string plan = Path.Combine(parent, ".fiddler-plans", id);
            SetBusy(true, "Preparing and independently reading the native hue candidate…");
            JsonNode result = await StaticStageDialog.Run(new[] { "prepare", "--source", _data.ClientData, "--stage", stage,
                "--edits", input, "--plan", plan, "--client-version", _data.ClientVersion }, _lifetime.Token, "hues.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Guard(); JsonNode row = result["records"][0];
            ShowComparison(Convert.FromBase64String((string)row["before_base64"]), Convert.FromBase64String((string)row["after_base64"]), prepared: true);
            _plan = plan; Applied = false; SetBusy(false, $"Verified hue 0x{_document.Id:X4}. Review the native name, bounds and palette above, then Apply to the staged folder.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task ApplyAsync()
    {
        if (_busy || !Prepared) return;
        try
        {
            Guard(); SetBusy(true, "Rechecking the candidate and applying to the stage…");
            await StaticStageDialog.Run(new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" }, _lifetime.Token, "hues.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Applied = true; SetBusy(false, "Applied and independently verified. The installation is unchanged; the previous stage can be recovered below.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task RecoverAsync()
    {
        if (_busy) return;
        try
        {
            SetBusy(true, "Recovering the previous stage…");
            await StaticStageDialog.Run(new[] { "recover", "--stage", Destination.Text, "--confirm" }, _lifetime.Token, "hues.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _plan = null; Applied = false; ShowComparison(_document.PreviewRecord(), _document.PreviewRecord(), prepared: false);
            SetBusy(false, "Previous stage recovered. Prepare again for a fresh review.");
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
        OkButtonText = Applied ? "Applied" : Prepared ? "Apply verified hue" : "Prepare verified copy";
        if (message != null) _status.Text = message;
        string journal = Destination.Text + ".fiddler-journal.json";
        bool recoverable = File.Exists(journal);
        if (recoverable)
            try { recoverable = (string)JsonNode.Parse(HueText.Read(journal))?["state"] != "recovered"; }
            catch { /* Keep recovery available so the stage tool can explain a damaged journal. */ }
        _recover.Visible = recoverable; _recover.Disabled = busy;
    }
    public override void _ExitTree() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
