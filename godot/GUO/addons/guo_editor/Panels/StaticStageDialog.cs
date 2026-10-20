#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Manual static-art export: prepare, inspect, explicitly apply, recover.</summary>
[Tool]
public partial class StaticStageDialog : ConfirmationDialog
{
    private readonly CancellationTokenSource _lifetime = new();
    private EditorData _data;
    private int _id;
    private string _image;
    private string _plan;
    private LineEdit _destination;
    private Label _status;
    private Button _recover;
    private bool _busy;

    public static StaticStageDialog Open(EditorData data, int id, Image preview)
    {
        var dialog = new StaticStageDialog();
        dialog.Build(data, id, preview);
        // Owned by the plugin's asset view, so teardown/reload cancels jobs
        // and frees this window along with the rest of the editor workspace.
        GuoEditorPlugin.AssetsMain.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(640, 540));
        return dialog;
    }

    public void Build(EditorData data, int id, Image preview)
    {
        _data = data;
        _id = id;
        _image = data.Assets.PathOf(AssetKind.Static, id);
        Title = $"Export static 0x{id:X4} to a verified stage";
        DialogHideOnOk = false;
        OkButtonText = "Prepare verified copy";
        CancelButtonText = "Close";
        Confirmed += () => _ = Execute();
        Canceled += QueueFree;
        CloseRequested += QueueFree;
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        AddChild(layout);
        layout.AddChild(new Label { Text = "Project replacement", HorizontalAlignment = HorizontalAlignment.Center });
        if (preview != null)
        {
            layout.AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(preview),
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(0, 200),
            });
        }
        layout.AddChild(new Label { Text = "Staged data folder" });
        _destination = new LineEdit { Text = Path.Combine(data.Assets.Root, "staged-static-art") };
        _destination.TextChanged += _ => InvalidatePreview();
        layout.AddChild(_destination);
        layout.AddChild(new Label
        {
            Text = "Prepare creates and verifies a separate copy. Apply replaces this ID in the stage.\n"
                + "The client installation stays read-only. The previous stage is retained for recovery.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(580, 0),
        });
        _status = new Label { Text = "Ready to prepare. No staged files have changed.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(580, 60) };
        layout.AddChild(_status);
        _recover = new Button { Text = "Recover previous stage…", CustomMinimumSize = new Vector2(0, 40) };
        _recover.Pressed += ConfirmRecovery;
        layout.AddChild(_recover);
        UpdateRecovery();
    }

    private void InvalidatePreview()
    {
        if (_busy) return;
        _plan = null;
        OkButtonText = "Prepare verified copy";
        _status.Text = "Destination changed. Prepare again before applying.";
        GetOkButton().Disabled = false;
        UpdateRecovery();
    }

    private void UpdateRecovery() => _recover.Disabled = _busy
        || !File.Exists(_destination.Text + ".fiddler-journal.json");

    private async Task Execute()
    {
        if (_busy) return;
        string stage = _destination.Text;
        if (string.IsNullOrWhiteSpace(stage))
        {
            _status.Text = "Choose a staged data folder.";
            return;
        }
        bool apply = _plan != null;
        if (!apply)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(stage));
            _plan = Path.Combine(parent, ".fiddler-plans", Guid.NewGuid().ToString("N"));
        }
        string plan = _plan;
        string[] args = apply
            ? new[] { "apply", "--plan", Path.Combine(plan, "plan.json"), "--confirm" }
            : new[] { "prepare", "--source", _data.ClientData, "--stage", stage,
                "--image", _image, "--id", _id.ToString(), "--plan", plan,
                "--client-version", _data.ClientVersion };
        SetBusy(true, apply ? "Rechecking and applying…" : "Preparing a separate copy and checking it with the independent reader…");
        try
        {
            JsonNode result = await Run(args, _lifetime.Token);
            if (!GodotObject.IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _status.Text = apply
                ? "Applied and independently verified. The stage is ready; the project preview remains unchanged."
                : $"Verified static 0x{_id:X4}: {result["width"]}×{result["height"]}.\n"
                    + "Apply will replace this ID in the staged folder shown above.";
            OkButtonText = apply ? "Applied" : "Apply verified replacement";
            SetBusy(false);
            GetOkButton().Disabled = apply;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!GodotObject.IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
            _plan = null;
            SetBusy(false, e.Message);
            OkButtonText = "Prepare verified copy";
        }
    }

    private void ConfirmRecovery()
    {
        var confirmation = new ConfirmationDialog
        {
            Title = "Recover the previous stage?",
            DialogText = "The current stage will be retained separately. The previous stage will become current again.",
            OkButtonText = "Recover previous stage",
        };
        AddChild(confirmation);
        confirmation.Canceled += confirmation.QueueFree;
        confirmation.Confirmed += async () =>
        {
            confirmation.QueueFree();
            SetBusy(true, "Recovering the previous stage…");
            try
            {
                await Run(new[] { "recover", "--stage", _destination.Text, "--confirm" }, _lifetime.Token);
                if (!GodotObject.IsInstanceValid(this) || _lifetime.IsCancellationRequested) return;
                _plan = null;
                SetBusy(false, "Previous stage recovered. Prepare again to apply another replacement.");
                OkButtonText = "Prepare verified copy";
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (GodotObject.IsInstanceValid(this) && !_lifetime.IsCancellationRequested) SetBusy(false, e.Message);
            }
        };
        confirmation.PopupCentered();
    }

    private void SetBusy(bool busy, string message = null)
    {
        _busy = busy;
        _destination.Editable = !busy;
        GetOkButton().Disabled = busy;
        if (message != null) _status.Text = message;
        UpdateRecovery();
    }

    private static async Task<JsonNode> Run(string[] args, CancellationToken token)
    {
        var start = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = EditorData.RepoRoot,
        };
        start.ArgumentList.Add(Path.Combine(EditorData.RepoRoot, "tools", "fiddler_parity", "replacement.py"));
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!token.IsCancellationRequested) throw new IOException("Stage operation timed out. Inspect its recovery journal before retrying.");
            throw;
        }
        string stdout = await output;
        string stderr = await errors;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(stderr)
            ? "Stage operation failed. Run launchers/dev/fiddler_parity_doctor.bat --build." : stderr.Trim());
        return JsonNode.Parse(stdout) ?? throw new IOException("Stage tool returned no result.");
    }

    public override void _ExitTree()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
#endif
