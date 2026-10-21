#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Native sound review, audition and explicit recoverable publication.</summary>
[Tool]
public partial class SoundStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private SoundPanel _panel;
    private EditorData _data;
    private SoundDocument _document;
    private string _token, _plan, _mapping, _packaging;
    private bool _busy;
    private VBoxContainer _comparison;
    private Label _status;
    private TextEdit _aliases;
    private Button _recover;
    private byte[] _currentPcm;
    internal LineEdit Destination { get; private set; }
    internal bool Prepared => _plan != null && !Applied;
    internal bool Applied { get; private set; }
    internal bool Busy => _busy;
    internal string Status => _status.Text;
    internal Task Operation { get; private set; } = Task.CompletedTask;

    internal static SoundStageDialog Open(SoundPanel panel, EditorData data, SoundDocument document)
    {
        panel.RequireNativeContext(document); document.RequireNativeReview(document.NativeReviewToken);
        var dialog = new SoundStageDialog(); dialog.Build(panel, data, document); panel.AddChild(dialog);
        dialog.PopupCenteredClamped((Vector2I)(new Vector2(760, 520) * EditorInterface.Singleton.GetEditorScale()), 0.9f); return dialog;
    }
    private void Build(SoundPanel panel, EditorData data, SoundDocument document)
    {
        _panel = panel; _data = data; _document = document; _token = document.NativeReviewToken;
        _packaging = data.Files.IsUOPInstallation && File.Exists(data.Files.GetUOFilePath("soundLegacyMUL.uop")) ? "uop" : "mul";
        Title = $"Export verified sound stage · 0x{document.Id:X4}"; DialogHideOnOk = false;
        OkButtonText = "Prepare verified copy"; CancelButtonText = "Close";
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); AddChild(body);
        body.AddChild(new Label { Text = "Staged data folder" });
        Destination = new LineEdit { Text = Path.Combine(document.Project, "staged-sounds"), AccessibilityName = "Native sound stage destination" }; body.AddChild(Destination);
        Destination.TextChanged += _ => { if (_busy) return; _panel.Stop(); _plan = null; Applied = false; _aliases.Visible = false; ShowComparison(null); SetBusy(false, "Destination changed. Prepare a fresh verified copy."); };
        _comparison = new VBoxContainer(); body.AddChild(_comparison); ShowComparison(null);
        _aliases = new TextEdit { Editable = false, Visible = false, WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 80) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = "Physical sound row and all affected aliases" }; body.AddChild(_aliases);
        _status = AssetUi.Label("Prepare verifies a private native copy. Apply changes the staged folder; the installation stays read-only."); body.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", TooltipText = "Restore the prior stage and retain the candidate separately" }; body.AddChild(_recover);
        _recover.Pressed += () => Confirm(true);
        Confirmed += () => { if (_busy || Applied) return; if (!Prepared) Operation = PrepareAsync(); else Confirm(false); };
        Canceled += QueueFree; CloseRequested += QueueFree; SetBusy(false);
    }
    private void Confirm(bool recover)
    {
        var confirmation = new ConfirmationDialog { Title = recover ? "Recover previous sound stage?" : "Apply verified sound to stage?",
            DialogText = recover ? "Restore the prior stage or its absence? The current candidate will be retained separately." : _mapping + "\nApply the reviewed name and PCM to this staged folder?",
            DialogAutowrap = true, OkButtonText = recover ? "Recover previous stage" : "Apply to stage" };
        AddChild(confirmation); confirmation.Canceled += confirmation.QueueFree;
        confirmation.Confirmed += () => { confirmation.QueueFree(); Operation = recover ? RecoverAsync() : ApplyAsync(); };
        confirmation.PopupCenteredClamped((Vector2I)(new Vector2(700, 240) * EditorInterface.Singleton.GetEditorScale()), 0.9f);
    }
    private void Guard() { _panel.RequireNativeContext(_document); _document.RequireNativeReview(_token); }
    private void ShowComparison(JsonNode result)
    {
        foreach (Node child in _comparison.GetChildren()) { _comparison.RemoveChild(child); child.QueueFree(); }
        var columns = new HBoxContainer(); columns.AddThemeConstantOverride("separation", 24); _comparison.AddChild(columns);
        foreach (bool current in result == null ? new[] { false } : new[] { true, false })
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; columns.AddChild(column);
            JsonNode row = result?[current ? "current" : "exported"];
            column.AddChild(new Label { Text = current ? "Current stage" : "Native export" });
            var name = new LineEdit { Editable = false, Text = row == null ? _document.CurrentName : (string)row["name"], AccessibilityName = current ? "Current native sound name" : "Exported native sound name" }; column.AddChild(name);
            int samples = row == null ? _document.Samples : (int)row["samples"];
            column.AddChild(AssetUi.Label($"{samples:N0} samples · {samples / 22050.0:0.000} s\nPCM · mono · 16-bit · 22050 Hz"));
            var audio = new HBoxContainer(); column.AddChild(audio);
            audio.AddChild(AssetUi.Action("Play", current ? "Audition current staged PCM" : "Audition reviewed export PCM", () =>
            { try { Guard(); _panel.PlayPcm(current ? _currentPcm : _document.Pcm()); } catch (Exception e) { Fail(e); } }));
            audio.AddChild(AssetUi.Action("Stop", "Stop sound audition", _panel.Stop));
        }
    }
    internal async Task PrepareAsync()
    {
        if (_busy) return;
        try
        {
            Guard(); _panel.Stop();
            string stage = EditorPaths.OutsideInstall(Destination.Text, _data.ClientData);
            if (EditorPaths.Real(stage) == EditorPaths.Real(_data.ClientData)) throw new IOException("Choose a folder outside the active sound library.");
            string parent = Path.GetDirectoryName(stage), id = Guid.NewGuid().ToString("N");
            string input = EditorPaths.OutsideInstall(Path.Combine(parent, ".fiddler-inputs", id + ".json"), _data.ClientData);
            string pcm = EditorPaths.OutsideInstall(Path.Combine(parent, ".fiddler-inputs", id + ".pcm"), _data.ClientData);
            Directory.CreateDirectory(Path.GetDirectoryName(input)); Guard(); File.WriteAllBytes(pcm, _document.Pcm());
            HueText.Write(input, _data.ClientData, Encoding.UTF8.GetBytes(_document.NativeStageInput(_token, _packaging, pcm).ToJsonString()), Guard);
            string plan = Path.Combine(parent, ".fiddler-plans", id);
            SetBusy(true, "Resolving sound aliases and independently verifying native name and PCM…");
            JsonNode result = await StaticStageDialog.Run(new[] { "prepare", "--source", _data.ClientData, "--stage", stage,
                "--edits", input, "--plan", plan, "--client-version", _data.ClientVersion }, _lifetime.Token, "sounds.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            Guard(); _currentPcm = TextureDocument.Read(Path.Combine(plan, "current.pcm"), 64 * 1024 * 1024); SoundWave.ValidateLength(_currentPcm.Length);
            if (Convert.ToHexString(SHA256.HashData(_currentPcm)).ToLowerInvariant() != (string)result["current"]["pcm_sha256"])
                throw new IOException("Current-stage audition PCM changed. Prepare again.");
            ShowComparison(result); int physical = (int)result["physical_id"], count = result["affected_ids"].AsArray().Count;
            _mapping = $"Sound 0x{_document.Id:X4} → physical row 0x{physical:X4}. {count} visible ID(s) affected.";
            _aliases.Text = _mapping + "\n" + string.Join(", ", result["affected_ids"].AsArray().Select(n => $"0x{(int)n:X4}")); _aliases.Visible = true;
            _plan = plan; Applied = false;
            SetBusy(false, (int)result["appended_bytes"] == 0 ? "Verified identity export: native bytes stay unchanged. Review, then Apply." : "Verified native replacement. Audition both sounds and review every affected ID, then Apply.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    internal async Task ApplyAsync()
    {
        if (_busy || !Prepared) return;
        try
        {
            Guard(); _panel.Stop(); SetBusy(true, "Rechecking and applying the native sound candidate…");
            await StaticStageDialog.Run(new[] { "apply", "--plan", Path.Combine(_plan, "plan.json"), "--confirm" }, _lifetime.Token, "sounds.py");
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
            _panel.Stop(); SetBusy(true, "Recovering the previous stage…");
            await StaticStageDialog.Run(new[] { "recover", "--stage", Destination.Text, "--confirm" }, _lifetime.Token, "sounds.py");
            if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _plan = null; Applied = false; _aliases.Visible = false; ShowComparison(null); SetBusy(false, "Previous stage recovered. Prepare again for a fresh review.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    private void Fail(Exception error) { if (!IsInstanceValid(this) || _lifetime.IsCancellationRequested) return; _panel.Stop(); _plan = null; SetBusy(false, error.Message); }
    private void SetBusy(bool busy, string message = null)
    {
        _busy = busy; Destination.Editable = !busy; GetOkButton().Disabled = busy || Applied;
        OkButtonText = Applied ? "Applied" : Prepared ? "Apply verified sound" : "Prepare verified copy";
        if (message != null) _status.Text = message;
        string journal = Destination.Text + ".fiddler-journal.json"; bool recoverable = File.Exists(journal);
        if (recoverable) try { recoverable = (string)JsonNode.Parse(HueText.Read(journal))?["state"] != "recovered"; } catch { }
        _recover.Visible = recoverable; _recover.Disabled = busy;
    }
    public override void _ExitTree() { _panel.Stop(); _lifetime.Cancel(); _lifetime.Dispose(); }
}
#endif
