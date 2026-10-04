#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Art dock (ADR-0029): image services (ComfyUI, Retro Diffusion) with the asset on show in the UO
/// Inspector bound as input, a gallery of what came back, "Import to overlay" with provenance, and the
/// exchange-folder watcher that imports what Pixelorama and Pinta save. Owns worker tasks; <see cref="Shutdown"/>
/// cancels them before an assembly reload.
/// </summary>
[Tool]
public partial class ArtDock : EditorDock
{
    private EditorData _data;
    private Func<Inspection> _source = () => null;
    private CancellationTokenSource _cts = new();
    private Task<ImageResult> _running;
    private volatile float _progressValue;
    private OptionButton _providerPick, _workflowPick;
    private LineEdit _url, _prompt;
    private CheckBox _useAsset;
    private Label _tools, _status, _target;
    private ProgressBar _bar;
    private ItemList _gallery;
    private Button _queue, _import;
    private readonly List<Image> _images = new();
    private ImageRequest _lastRequest;
    private ImageResult _lastResult;
    private AssetKind? _targetKind;
    private int _targetId = -1;
    private double _sinceWatch;
    private bool _ready;

    /// <summary>The last ComfyUI/Retro Diffusion result, for the smoke.</summary>
    public ImageResult LastResult => _lastResult;

    public int GalleryCount => _images.Count;

    public string Status => _status?.Text ?? "";

    public ArtDock()
    {
        Name = "UOArt";
        Title = "Art";
        LayoutKey = "guo_art";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Vertical | DockLayout.Floating;
        IconName = "Image";
    }

    public void Attach(EditorData data, Func<Inspection> source)
    {
        _data = data;
        _source = source ?? (() => null);
    }

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        _tools = new Label { Text = ExternalTools.Describe(), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_tools);

        var outside = new HBoxContainer();
        root.AddChild(outside);
        foreach (bool pinta in new[] { false, true })
        {
            bool usePinta = pinta;
            var edit = new Button { Text = pinta ? "Edit in Pinta" : "Edit in Pixelorama" };
            edit.Pressed += () => EditInspected(usePinta);
            outside.AddChild(edit);
        }
        var setup = new Button { Text = "Art tools / setup" };
        setup.Pressed += ShowToolHelp;
        outside.AddChild(setup);
        var exchange = new Button { Text = "Open exchange folder" };
        exchange.Pressed += () =>
        {
            string folder = EditorData.Setting("UO_ART_EXCHANGE", Path.Combine(EditorData.RepoRoot, "build", "art_exchange"));
            Directory.CreateDirectory(folder); OS.ShellOpen(folder);
        };
        outside.AddChild(exchange);

        var row = new HBoxContainer();
        root.AddChild(row);
        _providerPick = new OptionButton();
        _providerPick.AddItem("ComfyUI");
        _providerPick.AddItem("Retro Diffusion");
        row.AddChild(_providerPick);
        _url = new LineEdit
        {
            Text = EditorData.Setting("UO_COMFY_URL", "http://127.0.0.1:8188"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "server URL",
            TooltipText = "ComfyUI's address (UO_COMFY_URL). Retro Diffusion uses the URL of its endpoint in the AI dock, or its own address.",
        };
        row.AddChild(_url);

        var wf = new HBoxContainer();
        root.AddChild(wf);
        wf.AddChild(new Label { Text = "Workflow" });
        _workflowPick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        wf.AddChild(_workflowPick);
        var refresh = new Button { Text = "Refresh" };
        refresh.Pressed += RefreshWorkflows;
        wf.AddChild(refresh);

        _prompt = new LineEdit { PlaceholderText = "prompt", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddChild(_prompt);

        var bind = new HBoxContainer();
        root.AddChild(bind);
        _useAsset = new CheckBox { Text = "Use the inspected asset as input" };
        bind.AddChild(_useAsset);
        _target = new Label { Text = "" };
        bind.AddChild(_target);

        var go = new HBoxContainer();
        root.AddChild(go);
        _queue = new Button { Text = "Queue" };
        _queue.Pressed += () => _ = QueueFromUi();
        go.AddChild(_queue);
        _bar = new ProgressBar { SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxValue = 1, Step = 0.01, CustomMinimumSize = new Vector2(120, 0) };
        go.AddChild(_bar);

        _gallery = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
            IconMode = ItemList.IconModeEnum.Top,
            FixedIconSize = new Vector2I(96, 96),
            MaxColumns = 0,
            // Never filter pixel art.
            TextureFilter = TextureFilterEnum.Nearest,
        };
        root.AddChild(_gallery);

        _import = new Button { Text = "Import to overlay" };
        _import.Pressed += () => Report(ImportSelected());
        root.AddChild(_import);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_status);
        RefreshWorkflows();
    }

    private void RefreshWorkflows()
    {
        _workflowPick.Clear();
        foreach (string f in ComfyUiProvider.Workflows(WorkflowFolder))
        {
            _workflowPick.AddItem(Path.GetFileName(f));
            _workflowPick.SetItemMetadata(_workflowPick.ItemCount - 1, f);
        }

        if (_workflowPick.ItemCount == 0)
        {
            _workflowPick.AddItem("(no workflows: put API-format .json files in the workflow folder)");
            _workflowPick.Disabled = true;
        }
        else
        {
            _workflowPick.Disabled = false;
        }
    }

    private void EditInspected(bool pinta)
    {
        Inspection ins = _source();
        if (ins?.ArtKind == null || ins.Image == null)
        {
            Report("Select an asset in Assets > Art or Gumps, or inspect terrain/a static in World first.");
            return;
        }
        try
        {
            Report(AssetActions.EditIn(_data, ins.ArtKind.Value, ins.ArtId, ins.Image, pinta)
                ?? (pinta ? "Pinta opened. Save the exported PNG; GUO imports it automatically."
                    : "Pixelorama opened. Choose GUO > Save back to GUO; GUO imports it automatically."));
        }
        catch (Exception ex) { Report(ex.Message); }
    }

    private void ShowToolHelp()
    {
        _tools.Text = ExternalTools.Describe();
        var dialog = new AcceptDialog
        {
            Title = "Art tools — setup and round trip",
            DialogText = ExternalTools.Describe() + "\n\nPixelorama setup (from the repository):\npython tools/pixelorama/run.py fetch\n"
                + "Or set UO_PIXELORAMA in launchers/_shared/config.local.bat to an installed executable.\n"
                + "Pinta setup: winget install Pinta.Pinta\nOr set UO_PINTA in config.local.bat. Restart GUO after changing paths.\n\n"
                + "1. Select an asset in Assets > Art / Gumps, or inspect a World tile.\n"
                + "2. Click Edit in Pixelorama or Edit in Pinta. These open separate windows.\n"
                + "3. Pixelorama: GUO > Save back to GUO. Pinta: save the exported PNG.\n"
                + "4. The Art panel reports the import; the world project uses the replacement.\n\n"
                + "Other image editors: use Inspector > Save PNG, edit it, then Import PNG.\n"
                + "ComfyUI: choose a workflow and endpoint below, Queue, select a result, Import to overlay.\n"
                + "Retro Diffusion: configure its endpoint/key in AI, then select it below. Queue may incur provider charges.\n"
                + "Animation round trips and embedded Pixelorama tabs are not supported."
        };
        AddChild(dialog); dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();
    }

    public static string WorkflowFolder
    {
        get
        {
            string f = EditorData.Setting("UO_COMFY_WORKFLOWS", "");
            return f.Length > 0 ? f : Path.Combine(ArtExchange.Root, "workflows");
        }
    }

    public override void _Process(double delta)
    {
        if (_bar != null)
        {
            _bar.Value = _progressValue;
        }

        if (_running != null && _running.IsCompleted)
        {
            Task<ImageResult> t = _running;
            _running = null;
            Finish(t.IsFaulted ? new ImageResult { Error = t.Exception?.GetBaseException().Message } : t.Result);
        }

        _sinceWatch += delta;
        if (_sinceWatch >= 1.0 && _data?.Assets != null)
        {
            _sinceWatch = 0;
            foreach (ArtExchange.Outcome o in ArtExchange.Poll(_data))
            {
                Report(o.Imported ? $"{o.Stem}: imported from {o.Source}" : $"{o.Stem}: refused ({o.Why})");
            }
        }

        if (_target != null)
        {
            Inspection ins = _source();
            _target.Text = ins?.ArtKind != null ? $"{ArtSidecar.KindName(ins.ArtKind.Value)} 0x{ins.ArtId:X4}" : "(nothing to bind: pick art or a gump)";
        }
    }

    private void Report(string text)
    {
        if (_status != null)
        {
            _status.Text = text ?? "";
        }

        if (text != null)
        {
            GD.Print($"[GUO editor] art: {text}");
        }
    }

    private async Task QueueFromUi()
    {
        IImageProvider provider = MakeProvider(_providerPick.Selected == 1, _url.Text, out string why);
        if (provider == null)
        {
            Report(why);
            return;
        }

        string wf = _workflowPick.ItemCount > 0 && !_workflowPick.Disabled ? (string)_workflowPick.GetItemMetadata(_workflowPick.Selected) : "";
        var req = BuildRequest(_prompt.Text, wf, _useAsset.ButtonPressed ? _source() : null);
        ImageResult r = await RunAsync(provider, req);
        Report(r.Error ?? $"{r.Pngs.Count} image(s) from {provider.Name}");
    }

    /// <summary>A provider for the picker's choice. Retro Diffusion's key comes from the AI dock's endpoint book.</summary>
    public static IImageProvider MakeProvider(bool retroDiffusion, string url, out string why)
    {
        why = null;
        if (!retroDiffusion)
        {
            return new ComfyUiProvider(url);
        }

        var book = new EndpointBook();
        EndpointBook.Entry e = book.Entries.Find(x => x.Name == RetroDiffusionProvider.EndpointName);
        if (e == null)
        {
            why = $"Retro Diffusion needs an endpoint named '{RetroDiffusionProvider.EndpointName}' in the AI dock, with its key";
            return null;
        }

        return new RetroDiffusionProvider(string.IsNullOrWhiteSpace(e.Url) ? RetroDiffusionProvider.DefaultUrl : e.Url, () => book.KeyFor(e));
    }

    /// <summary>The request for a prompt, a workflow file and (optionally) the inspected asset as input.</summary>
    public ImageRequest BuildRequest(string prompt, string workflowPath, Inspection bound)
    {
        var req = new ImageRequest { Prompt = prompt ?? "", WorkflowPath = workflowPath ?? "" };
        _targetKind = bound?.ArtKind;
        _targetId = bound?.ArtId ?? -1;
        if (bound?.Image != null && bound.ArtKind != null)
        {
            req.InputPng = bound.Image.SavePngToBuffer();
            req.Width = bound.Image.GetWidth();
            req.Height = bound.Image.GetHeight();
            req.InputName = $"{(IsReplaced(bound) ? "overlay" : "client")}:{ArtSidecar.KindName(bound.ArtKind.Value)}:0x{bound.ArtId:X4}";
            // The input is the client's pixels unless an original already replaced them.
            req.InputIsClientArt = !IsReplaced(bound) || (new AssetProvenance(_data.Assets).Get(_data.Assets.RelativePathOf(bound.ArtKind.Value, bound.ArtId))?.DerivedFromClientArt ?? true);
        }

        return req;
    }

    private bool IsReplaced(Inspection ins) => _data?.Assets != null && ins.ArtKind != null && _data.Assets.Has(ins.ArtKind.Value, ins.ArtId);

    /// <summary>Runs a request on a worker thread and fills the gallery; the task completes with the result.</summary>
    public Task<ImageResult> RunAsync(IImageProvider provider, ImageRequest req)
    {
        _lastRequest = req;
        _providerUsed = provider;
        _progressValue = 0;
        Report($"queued on {provider.Name}...");
        CancellationToken ct = _cts.Token;
        var task = Task.Run(() => provider.RunAsync(req, p => _progressValue = (float)p, ct), ct);
        var tcs = new TaskCompletionSource<ImageResult>();
        _running = tcs.Task;
        task.ContinueWith(t =>
        {
            tcs.SetResult(t.IsFaulted ? new ImageResult { Error = t.Exception?.GetBaseException().Message } : t.IsCanceled ? new ImageResult { Error = "cancelled" } : t.Result);
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    private IImageProvider _providerUsed;

    private void Finish(ImageResult r)
    {
        _lastResult = r;
        _images.Clear();
        _gallery?.Clear();
        foreach (byte[] png in r.Pngs)
        {
            var img = new Image();
            if (img.LoadPngFromBuffer(png) == Error.Ok)
            {
                _images.Add(img);
                _gallery?.AddItem($"{img.GetWidth()}x{img.GetHeight()}", ImageTexture.CreateFromImage(img));
            }
        }

        if (_gallery != null && _images.Count > 0)
        {
            _gallery.Select(0);
        }

        Report(r.Error ?? $"{_images.Count} image(s) ready; pick one and Import to overlay");
    }

    /// <summary>
    /// Imports the selected gallery image as the bound asset, with provenance: the tool, model, workflow,
    /// seed and inputs, and derived-from-client-art when the input was the client's own pixels.
    /// Returns a status line (the reason when it did not import).
    /// </summary>
    public string ImportSelected(int index = -1)
    {
        if (_data?.Assets == null)
        {
            return "no world project is open";
        }

        if (index < 0)
        {
            int[] sel = _gallery?.GetSelectedItems() ?? Array.Empty<int>();
            index = sel.Length > 0 ? sel[0] : 0;
        }

        if (index >= _images.Count || _lastResult == null)
        {
            return "nothing in the gallery";
        }

        if (_targetKind == null)
        {
            return "no target: show a land tile, static or gump in the UO Inspector and tick 'Use the inspected asset' before queueing";
        }

        var prov = new ArtProvenance
        {
            Tool = _providerUsed?.Id ?? "image-service",
            Model = _lastResult.Model,
            Workflow = _lastResult.Workflow,
            Seed = _lastResult.Seed >= 0 ? _lastResult.Seed : null,
            DerivedFromClientArt = _lastRequest?.InputIsClientArt ?? false,
        };
        if (!string.IsNullOrEmpty(_lastRequest?.InputName))
        {
            prov.Inputs.Add(_lastRequest.InputName);
        }

        var notes = new List<string>();
        string why = ArtExchange.ImportImage(_data, _targetKind.Value, _targetId, _images[index], prov, notes);
        return why == null
            ? $"imported as {ArtSidecar.KindName(_targetKind.Value)} 0x{_targetId:X4} ({string.Join("; ", notes)})"
            : $"refused: {why}";
    }

    /// <summary>Cancels running work and releases the HTTP clients; called before a reload or when the dock closes.</summary>
    public void Shutdown()
    {
        _cts.Cancel();
        (_providerUsed as IDisposable)?.Dispose();
        _providerUsed = null;
        _cts = new CancellationTokenSource();
        _running = null;
    }
}
#endif
