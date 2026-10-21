#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Reviewed native texture export, including physical row and shared aliases.</summary>
[Tool]
public partial class TextureStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private TexturePanel _panel;
    private EditorData _data;
    private TextureDocument _document;
    private string _token, _plan, _mappingSummary;
    private bool _busy;
    private VBoxContainer _comparison;
    private Label _status;
    private TextEdit _aliases;
    private Button _recover;
    internal LineEdit Destination { get; private set; }
    internal bool Prepared => _plan != null && !Applied;
    internal bool Applied { get; private set; }
    internal bool Busy => _busy;
    internal string Status => _status.Text;
    internal Task Operation { get; private set; } = Task.CompletedTask;

    internal static TextureStageDialog Open(TexturePanel panel, EditorData data, TextureDocument document)
    {
        var dialog = new TextureStageDialog(); dialog.Build(panel, data, document); panel.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(720, 560) * EditorInterface.Singleton.GetEditorScale()), 0.9f); return dialog;
    }
    internal void Build(TexturePanel panel, EditorData data, TextureDocument document)
    {
        _panel = panel; _data = data; _document = document; _token = document.ReviewToken; Guard();
        Title = $"Export verified texture stage · 0x{document.Id:X4}"; DialogHideOnOk = false;
        OkButtonText = "Prepare verified copy"; CancelButtonText = "Close";
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); AddChild(body);
        body.AddChild(new Label { Text = "Staged data folder" });
        Destination = new LineEdit { Text = Path.Combine(document.Project, "staged-textures"), AccessibilityName = "Texture stage destination" }; body.AddChild(Destination);
        Destination.TextChanged += _ => { if (_busy) return; _plan = null; Applied = false; _aliases.Visible = false; SetBusy(false, "Destination changed. Prepare a fresh verified copy."); };
        _comparison = new VBoxContainer(); body.AddChild(_comparison); ShowComparison(null, document.Record());
        _aliases = new TextEdit { Editable = false, Visible = false, WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 72) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = "Physical texture row and all affected aliases" }; body.AddChild(_aliases);
        _status = AssetUi.Label("Prepare reads the destination and verifies a private candidate. Apply changes the staged copy; the installation stays read-only."); body.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", TooltipText = "Restore the previous stage and retain the candidate separately" }; body.AddChild(_recover);
        _recover.Pressed += () => Confirm("Recover previous texture stage?", "Restore the previous stage? The current candidate will be retained separately.", () => Operation = RecoverAsync());
        Confirmed += () =>
        {
            if (_busy || Applied) return;
            if (!Prepared) { Operation = PrepareAsync(); return; }
            Confirm("Apply verified texture to stage?", _mappingSummary + "\nApply this reviewed replacement to the staged folder?", () => Operation = ApplyAsync());
        };
        Canceled += QueueFree; CloseRequested += QueueFree; SetBusy(false);
    }
    private void Confirm(string title, string text, Action action)
    {
        var confirmation = new ConfirmationDialog { Title = title, DialogText = text,
            OkButtonText = title.StartsWith("Recover") ? "Recover previous stage" : "Apply to stage" };
        AddChild(confirmation); confirmation.Canceled += confirmation.QueueFree;
        confirmation.Confirmed += () => { confirmation.QueueFree(); action(); }; confirmation.PopupCenteredClamped(new Vector2I(600, 240), 0.9f);
    }
    private void Guard()
    {
        if (!_panel.IsCurrentProject(_document)) throw new IOException("The project changed. Reopen texture export in the active project.");
        _document.RequireNativeReview(_token);
    }
    private void ShowComparison(byte[] before, byte[] after)
    {
        foreach (Node child in _comparison.GetChildren()) { _comparison.RemoveChild(child); child.QueueFree(); }
        var images = new GridContainer { Columns = before == null ? 1 : 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _comparison.AddChild(images);
        byte[][] records = before == null ? new[] { after } : new[] { before, after };
        foreach (byte[] record in records)
        {
            int size = TextureDocument.Dimension(record);
            images.AddChild(new Label { Text = $"{(record == before ? "Current stage" : "Native export")} · {size} × {size}" });
        }
        foreach (byte[] record in records)
        {
            using Image image = TextureDocument.Image(record);
            images.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(image), TextureFilter = Control.TextureFilterEnum.Nearest,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                CustomMinimumSize = new Vector2(128, 128) * EditorInterface.Singleton.GetEditorScale(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        }
    }
    internal async Task PrepareAsync()
    {
        if (_busy) return;
        try
        {
            Guard(); byte[] inputBytes = _document.NativeStageInput(_token);
            string stage = EditorPaths.OutsideInstall(Destination.Text, _data.ClientData);
            string parent = Path.GetDirectoryName(stage), id = Guid.NewGuid().ToString("N");
            string input = EditorPaths.OutsideInstall(Path.Combine(parent, ".fiddler-inputs", id + ".json"), _data.ClientData);
            Directory.CreateDirectory(Path.GetDirectoryName(input)); HueText.Write(input, _data.ClientData, inputBytes, Guard);
            string plan = Path.Combine(parent, ".fiddler-plans", id);
            SetBusy(true, "Resolving texture aliases and independently verifying the native candidate…");
            JsonNode result = await StaticStageDialog.Run(new[] { "prepare", "--source", _data.ClientData, "--stage", stage,
                "--edits", input, "--plan", plan, "--client-version", _data.ClientVersion }, _lifetime.Token, "textures.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Guard(); JsonNode row = result["records"][0];
            ShowComparison(Convert.FromBase64String((string)row["before_base64"]), Convert.FromBase64String((string)row["after_base64"]));
            int physical = (int)result["physical_id"], count = result["affected_ids"].AsArray().Count;
            _mappingSummary = $"Texture 0x{_document.Id:X4} → physical row 0x{physical:X4}. {count} visible ID(s) affected.";
            _aliases.Text = _mappingSummary + "\n" + string.Join(", ", result["affected_ids"].AsArray().Select(n => $"0x{(int)n:X4}")); _aliases.Visible = true;
            _plan = plan; Applied = false;
            SetBusy(false, (int)result["appended_bytes"] == 0 ? "Verified identity export: native bytes stay unchanged. Review affected IDs, then Apply." : "Verified native replacement. Review both textures and all affected IDs, then Apply.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task ApplyAsync()
    {
        if (_busy || !Prepared) return;
        try
        {
            Guard(); SetBusy(true, "Rechecking and applying the native texture candidate…");
            await StaticStageDialog.Run(new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" }, _lifetime.Token, "textures.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Applied = true; SetBusy(false, "Applied and independently verified. The source installation is unchanged; the prior stage can be recovered.");
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
            await StaticStageDialog.Run(new[] { "recover", "--stage", Destination.Text, "--confirm" }, _lifetime.Token, "textures.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _plan = null; Applied = false; _aliases.Visible = false; ShowComparison(null, _document.Record()); SetBusy(false, "Previous stage recovered. Prepare again for a fresh review.");
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
        OkButtonText = Applied ? "Applied" : Prepared ? "Apply verified texture" : "Prepare verified copy";
        if (message != null) _status.Text = message;
        string journal = Destination.Text + ".fiddler-journal.json";
        bool recoverable = File.Exists(journal);
        if (recoverable)
            try { recoverable = (string)JsonNode.Parse(HueText.Read(journal))?["state"] != "recovered"; }
            catch { }
        _recover.Visible = recoverable; _recover.Disabled = busy;
    }
    public override void _ExitTree() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
