#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

/// <summary>
/// The Layers dock's authoring side: capture the World tab's view of the
/// rect, paint WHERE the decal goes (the mask), repaint through ComfyUI
/// (Flux Klein edit), subtract the capture, keep only the masked part, and
/// save it as the layer's image. One quad, one image: no tiling anywhere.
/// </summary>
public partial class LayersDock
{
    private OptionButton _wfPick;
    private SpinBox _tol, _feather, _brush, _cfg, _steps;
    private CheckBox _erase;
    private TextureRect _extractView, _genView, _maskView;
    private Image _capture, _generated, _decalRaw, _decal, _mask;
    private bool _busy, _brushing, _stopped;
    private Control _workflowRow, _generationRow, _generatedGroup;
    private Label _maskHelp;
    private Button _generate;
    private readonly CancellationTokenSource _dockLife = new();
    private CancellationTokenSource _generation;
    private int _jobVersion, _captureVersion;
    private const string DoctorHelp = "Run launchers/comfy/doctor.bat to check ComfyUI and its API-format workflows.";

    // Internal component seams: production defaults are evaluated only after the gate.
    internal Func<List<string>> DiscoverWorkflows { get; set; } = () => ComfyUiProvider.Workflows(ArtDock.WorkflowFolder);
    internal Func<IImageProvider> CreateProvider { get; set; } = () => new ComfyUiProvider(EditorData.Setting("UO_COMFY_URL", "http://127.0.0.1:8188"));

    internal void ApplyAiFeatures()
    {
        if (!_ready || _stopped) return;
        bool enabled = AiFeatures.Enabled;
        _workflowRow.Visible = enabled;
        _generationRow.Visible = enabled;
        _generatedGroup.Visible = enabled;
        _promptLabel.Text = enabled ? "Prompt" : "Layer note";
        _maskHelp.Text = enabled ? "Paint where the decal shows, then Generate." : "Paint where the staged decal shows.";
        if (!enabled)
        {
            CancelGeneration();
            _wfPick.Clear();
            _wfPick.Disabled = true;
            Report(AiFeatures.DisabledMessage);
        }
        else RefreshAuthorWorkflows();
        UpdateGenerateButton();
    }

    private void UpdateGenerateButton()
    {
        if (_generate != null) _generate.Disabled = _stopped || !AiFeatures.Enabled || _busy || _wfPick.Disabled;
    }

    private void CancelGeneration()
    {
        ++_jobVersion;
        _generation?.Cancel();
        _generation = null; // The owning runner disposes its source after completion.
        _busy = false;
        UpdateGenerateButton();
    }

    public void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        ++_captureVersion;
        _dockLife.Cancel();
        CancelGeneration();
        _dockLife.Dispose();
        foreach (TextureRect view in new[] { _preview, _extractView, _genView, _maskView })
            if (GodotObject.IsInstanceValid(view)) view.Texture = null;
        _capture?.Dispose(); _capture = null;
        _generated?.Dispose(); _generated = null;
        _decalRaw?.Dispose(); _decalRaw = null;
        _decal?.Dispose(); _decal = null;
        _mask?.Dispose(); _mask = null;
        _brushing = false;
        _world = null;
    }

    public override void _ExitTree() => Shutdown();

    private void BuildAuthorUi(VBoxContainer left, VBoxContainer right)
    {
        var wf = new HBoxContainer();
        _workflowRow = wf;
        left.AddChild(wf);
        wf.AddChild(new Label { Text = "Workflow" });
        _wfPick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        wf.AddChild(_wfPick);
        var refresh = new Button { Text = "Refresh" };
        refresh.Pressed += RefreshAuthorWorkflows;
        wf.AddChild(refresh);

        var go = new HBoxContainer();
        left.AddChild(go);
        var fromArea = new Button { Text = "From area", TooltipText = "Copy the Area tool's selection into X0..Y1" };
        fromArea.Pressed += () => Report(AdoptArea());
        go.AddChild(fromArea);
        var capture = new Button { Text = "Capture area" };
        capture.Pressed += CaptureFromUi;
        go.AddChild(capture);
        var generation = new HBoxContainer();
        _generationRow = generation;
        left.AddChild(generation);
        var generate = new Button { Text = "Generate" };
        _generate = generate;
        generate.Pressed += GenerateFromUi;
        generation.AddChild(generate);
        generation.AddChild(new Label { Text = "Cfg", TooltipText = "Guidance: how hard the prompt pulls. Lower stays closer to the capture." });
        _cfg = new SpinBox { MinValue = 0.5, MaxValue = 20, Step = 0.5, Value = 5 };
        generation.AddChild(_cfg);
        generation.AddChild(new Label { Text = "Steps", TooltipText = "Sampler steps: more refines further (and slower)." });
        _steps = new SpinBox { MinValue = 1, MaxValue = 50, Step = 1, Value = 4 };
        generation.AddChild(_steps);

        var ex = new HBoxContainer();
        left.AddChild(ex);
        ex.AddChild(new Label { Text = "Tolerance" });
        _tol = new SpinBox { MinValue = 0, MaxValue = 255, Step = 1, Value = 12 };
        ex.AddChild(_tol);
        ex.AddChild(new Label { Text = "Feather" });
        _feather = new SpinBox { MinValue = 0, MaxValue = 64, Step = 1, Value = 2 };
        ex.AddChild(_feather);
        var reextract = new Button { Text = "Re-extract" };
        reextract.Pressed += () => { Reextract(); ShowWorkViews(); };
        ex.AddChild(reextract);

        var vw = new HBoxContainer();
        left.AddChild(vw);
        vw.AddChild(new Label { Text = "Brush" });
        _brush = new SpinBox { MinValue = 2, MaxValue = 128, Step = 2, Value = 16 };
        vw.AddChild(_brush);
        _erase = new CheckBox { Text = "Erase" };
        vw.AddChild(_erase);
        var save = new Button { Text = "Save to layer" };
        save.Pressed += () => Report(SaveDecalToLayer());
        vw.AddChild(save);

        var maskRow = new HBoxContainer();
        left.AddChild(maskRow);
        maskRow.AddChild(new Label { Text = "Mask:" });
        var fill = new Button { Text = "Fill" };
        fill.Pressed += () => { FillMask(true); };
        maskRow.AddChild(fill);
        var clear = new Button { Text = "Clear" };
        clear.Pressed += () => { FillMask(false); };
        maskRow.AddChild(clear);
        _maskHelp = new Label();
        maskRow.AddChild(_maskHelp);

        _extractView = WorkPreview(right, "Extracted");
        var generated = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _generatedGroup = generated;
        right.AddChild(generated);
        _genView = WorkPreview(generated, "Generated");
        _maskView = WorkPreview(right, "Mask");
        _maskView.GuiInput += OnBrushInput;
    }

    private static TextureRect WorkPreview(VBoxContainer right, string title)
    {
        right.AddChild(new Label { Text = title });
        var view = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 96),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        right.AddChild(view);
        return view;
    }

    private void RefreshAuthorWorkflows()
    {
        if (_wfPick == null)
        {
            return;
        }

        _wfPick.Clear();
        _wfPick.Disabled = true;
        if (_stopped || !AiFeatures.Enabled)
        {
            UpdateGenerateButton();
            return;
        }
        List<string> workflows;
        try
        {
            workflows = DiscoverWorkflows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("Could not read workflow files. " + DoctorHelp);
            UpdateGenerateButton();
            return;
        }
        int flux = -1, studio = -1;
        foreach (string f in workflows)
        {
            _wfPick.AddItem(Path.GetFileName(f));
            _wfPick.SetItemMetadata(_wfPick.ItemCount - 1, f);
            if (string.Equals(Path.GetFileName(f), "flux_klein_edit.json", StringComparison.OrdinalIgnoreCase))
            {
                flux = _wfPick.ItemCount - 1;
            }
            else if (string.Equals(Path.GetFileName(f), "studio_img2img.json", StringComparison.OrdinalIgnoreCase))
            {
                studio = _wfPick.ItemCount - 1;
            }
        }

        if (_wfPick.ItemCount > 0)
        {
            _wfPick.Selected = flux >= 0 ? flux : studio >= 0 ? studio : 0;
        }

        if (_wfPick.ItemCount == 0)
        {
            _wfPick.AddItem("(no workflows: put API-format .json files in the workflow folder)");
            _wfPick.Disabled = true;
        }
        else
        {
            _wfPick.Disabled = false;
        }
        UpdateGenerateButton();
    }

    private void CurrentRect(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Min((int)_x0.Value, (int)_x1.Value);
        x1 = Math.Max((int)_x0.Value, (int)_x1.Value);
        y0 = Math.Min((int)_y0.Value, (int)_y1.Value);
        y1 = Math.Max((int)_y0.Value, (int)_y1.Value);
    }

    /// <summary>
    /// Copies the World tab's Area tool selection into the form (and the
    /// facet). Capture reads the form, so without this it would shoot
    /// whatever stale numbers are there — usually the 0,0 default, i.e. one
    /// tile at the map corner.
    /// </summary>
    public string AdoptArea()
    {
        if (_world == null || !_world.IsBooted)
        {
            return "World tab has not booted: show it once first.";
        }

        if (_world.Area is not { } a)
        {
            return "No Area selection: pick the Area tool in the World tab and click two corners.";
        }

        _x0.Value = a.X0;
        _y0.Value = a.Y0;
        _x1.Value = a.X1;
        _y1.Value = a.Y1;
        _facet.Value = Math.Clamp(_world.Host.Facet, 0, 255);
        return $"Area {a.X0},{a.Y0}-{a.X1},{a.Y1} in the form; Capture area shoots that.";
    }

    private int CornerZ(int x, int y)
    {
        try
        {
            return _world?.Host?.World?.Map?.GetTileZ(x, y) ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Photographs the rect out of the World tab at the overlay's own plane
    /// (flat at the highest corner, exactly where RefreshMesh will lay the
    /// decal), resampled to a straight image: texture pixel to tile space to
    /// world pixels to the camera, bilinearly.
    /// </summary>
    private async void CaptureFromUi()
    {
        if (_stopped) return;
        if (_world == null || !_world.IsBooted)
        {
            Report("World tab has not booted: show it once first.");
            return;
        }

        if (!_world.IsVisibleInTree())
        {
            Report("Show the World tab first: a hidden view captures blank.");
            return;
        }

        CurrentRect(out int x0, out int y0, out int x1, out int y1);
        if (x0 == 0 && y0 == 0 && x1 == 0 && y1 == 0 && _world?.Area != null)
        {
            Report(AdoptArea() + " Capturing that.");
            CurrentRect(out x0, out y0, out x1, out y1);
        }

        if (x1 < x0 || y1 < y0)
        {
            Report("Empty rect: set X0..X1 and Y0..Y1 first, or pick an Area in the World tab.");
            return;
        }

        int captureVersion = ++_captureVersion;
        CancellationToken captureLife = _dockLife.Token;
        CancelGeneration();
        for (int i = 0; i < 3; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (captureLife.IsCancellationRequested || _stopped || captureVersion != _captureVersion ||
                !IsInsideTree() || !GodotObject.IsInstanceValid(_world) || !_world.IsBooted) return;
        }

        int w = Math.Clamp((x1 - x0 + 1) * 64, 256, 1024);
        int h = Math.Clamp((y1 - y0 + 1) * 64, 256, 1024);
        int ztop = Math.Max(Math.Max(CornerZ(x0, y0), CornerZ(x1, y0)), Math.Max(CornerZ(x0, y1), CornerZ(x1, y1)));
        var cam = _world.Host.Scene.Camera;
        if (cam == null)
        {
            Report("World tab has no camera yet.");
            return;
        }

        using var shot = _world.CaptureView();
        if (shot == null || shot.IsEmpty())
        {
            Report("World tab has no image yet: let it draw a frame and capture again.");
            return;
        }

        var scene = _world.Host.Scene;
        var off = scene.DrawOffset;
        var view = cam.ViewTransform;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var img = TerrainLayers.CaptureRect(shot, (tx, ty) =>
        {
            var s = view * new Godot.Vector2((tx - ty) * 22f - 22f - off.X, (tx + ty) * 22f - (ztop << 2) - 22f - off.Y);
            if (s.X < minX) minX = s.X;
            if (s.Y < minY) minY = s.Y;
            if (s.X > maxX) maxX = s.X;
            if (s.Y > maxY) maxY = s.Y;
            return (s.X, s.Y);
        }, x0, y0, x1, y1, ztop, w, h);

        ReplaceCapture(img);
        ShowWorkViews();
        string next = AiFeatures.Enabled ? "Paint the mask, then Generate." : "Paint the mask for a staged decal.";
        Report($"Captured {w}x{h} over ({x0},{y0})-({x1},{y1}); shot {shot.GetWidth()}x{shot.GetHeight()}, samples {minX:0}-{maxX:0},{minY:0}-{maxY:0}. {next}");
    }

    // Ownership transfers on the editor thread; replacing capture invalidates old jobs.
    internal void ReplaceCapture(Image img)
    {
        if (_stopped) { img?.Dispose(); return; }
        ++_captureVersion;
        CancelGeneration();
        _capture?.Dispose();
        _capture = img;
        _generated?.Dispose();
        _generated = null;
        _decalRaw?.Dispose();
        _decalRaw = null;
        _decal?.Dispose();
        _decal = null;
        ResetMask(img.GetWidth(), img.GetHeight());
    }

    /// <summary>Blank mask at capture size (white = decal shows).</summary>
    private void ResetMask(int w, int h)
    {
        _mask?.Dispose();
        _mask = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        _mask.Fill(new Color(0, 0, 0, 0));
    }

    private void FillMask(bool white)
    {
        if (_mask == null)
        {
            Report("Capture an area first: the mask matches the capture size.");
            return;
        }

        _mask.Fill(white ? new Color(1, 1, 1, 1) : new Color(0, 0, 0, 0));
        Recombine();
        ShowWorkViews();
    }

    private async void GenerateFromUi() => await RunGenerationAsync();

    // Called by both the UI and synthetic component proof on the editor thread.
    internal async Task RunGenerationAsync()
    {
        if (_stopped) return;
        if (!AiFeatures.Enabled) { Report(AiFeatures.DisabledMessage); return; }
        if (_busy) return;
        if (_capture == null) { Report("Capture the area first."); return; }
        string wf = _wfPick.ItemCount > 0 && !_wfPick.Disabled ? (string)_wfPick.GetItemMetadata(_wfPick.Selected) : "";
        if (string.IsNullOrEmpty(wf)) { Report("Pick an img2img workflow first. " + DoctorHelp); return; }

        using var job = CancellationTokenSource.CreateLinkedTokenSource(_dockLife.Token);
        using var life = AiFeatures.Link(job.Token);
        CancellationToken token = life.Token;
        int version = ++_jobVersion;
        bool Current() => !_stopped && !token.IsCancellationRequested && version == _jobVersion && AiFeatures.Enabled;
        _generation = job;
        _busy = true;
        UpdateGenerateButton();
        try
        {
            // Snapshot all Godot inputs before the provider runs; await resumes on the
            // editor synchronization context. Providers never receive Godot objects.
            var req = new ImageRequest
            {
                Prompt = _prompt.Text ?? "",
                WorkflowPath = wf,
                InputPng = _capture.SavePngToBuffer(),
                InputName = $"layer:{(_id.Text ?? "").Trim()}",
                Width = _capture.GetWidth(), Height = _capture.GetHeight(),
                Cfg = (float)_cfg.Value, Steps = (int)_steps.Value,
            };
            Report("Generating image...");
            IImageProvider provider = CreateProvider();
            using var ownedProvider = provider as IDisposable;
            ImageResult r = await Task.Run(() => provider.RunAsync(req, null, token), token);
            if (!Current()) return;
            if (r == null || r.Error != null) { Report("ComfyUI could not complete the image job. " + DoctorHelp); return; }
            if (r.Pngs.Count == 0) { Report("ComfyUI returned no images. " + DoctorHelp); return; }
            var gen = new Image();
            if (gen.LoadPngFromBuffer(r.Pngs[0]) != Error.Ok || gen.IsEmpty())
            {
                gen.Dispose();
                Report("ComfyUI returned an undecodable image. " + DoctorHelp);
                return;
            }
            if (gen.GetWidth() != req.Width || gen.GetHeight() != req.Height) gen.Resize(req.Width, req.Height);
            _generated?.Dispose();
            _generated = gen;
            Reextract();
            ShowWorkViews();
            Report($"{r.Pngs.Count} image(s), seed {r.Seed}; decal extracted under the mask. Save to layer.");
        }
        catch (OperationCanceledException)
        {
            if (Current()) Report("Image generation cancelled. Try Generate again.");
        }
        catch (Exception)
        {
            if (Current()) Report("ComfyUI could not complete the image job. " + DoctorHelp);
        }
        finally
        {
            if (!_stopped && version == _jobVersion)
            {
                _generation = null;
                _busy = false;
                UpdateGenerateButton();
            }
        }
    }

    private void Reextract()
    {
        _decalRaw?.Dispose();
        _decalRaw = null;
        if (_capture == null || _generated == null)
        {
            return;
        }

        _decalRaw = TerrainLayers.ExtractDecal(_capture, _generated, (float)_tol.Value, (float)_feather.Value);
        Recombine();
    }

    /// <summary>Final decal = raw extract masked by what the user painted.</summary>
    private void Recombine()
    {
        _decal?.Dispose();
        _decal = null;
        if (_decalRaw == null || _decalRaw.IsEmpty() || _mask == null || _mask.IsEmpty())
        {
            return;
        }

        int w = _decalRaw.GetWidth(), h = _decalRaw.GetHeight();
        if (_mask.GetWidth() != w || _mask.GetHeight() != h)
        {
            return;
        }

        var fin = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = _decalRaw.GetPixel(x, y);
                c.A *= _mask.GetPixel(x, y).A;
                fin.SetPixel(x, y, c);
            }
        }

        _decal = fin;
    }

    /// <summary>Refreshes all three work previews (extracted decal, generated image, mask).</summary>
    private void ShowWorkViews()
    {
        SetPreview(_extractView, _decal);
        SetPreview(_genView, _generated);
        using var mask = MaskView();
        SetPreview(_maskView, mask);
    }

    private static void SetPreview(TextureRect view, Image img)
    {
        if (view == null)
        {
            return;
        }

        view.Texture = img == null || img.IsEmpty() ? null : ImageTexture.CreateFromImage(img);
    }

    /// <summary>Refreshes just the mask preview (per brush stroke).</summary>
    private void UpdateMaskPreview()
    {
        using var mask = MaskView();
        SetPreview(_maskView, mask);
    }

    /// <summary>Dimmed capture with the painted mask in red: where the decal is allowed. Always a fresh image.</summary>
    private Image MaskView()
    {
        if (_capture == null || _capture.IsEmpty() || _mask == null || _mask.IsEmpty())
        {
            return null;
        }

        int w = _capture.GetWidth(), h = _capture.GetHeight();
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = _capture.GetPixel(x, y);
                float m = x < _mask.GetWidth() && y < _mask.GetHeight() ? _mask.GetPixel(x, y).A : 0f;
                img.SetPixel(x, y, new Color(c.R * 0.45f + 0.55f * m, c.G * 0.45f + 0.1f * m, c.B * 0.45f + 0.1f * m));
            }
        }

        return img;
    }

    private void OnBrushInput(InputEvent e)
    {
        if (_mask == null || _mask.IsEmpty())
        {
            return;
        }

        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            _brushing = mb.Pressed;
            if (_brushing)
            {
                PaintMaskAt(mb.Position);
            }
        }
        else if (e is InputEventMouseMotion mm && _brushing && mm.ButtonMask.HasFlag(MouseButtonMask.Left))
        {
            PaintMaskAt(mm.Position);
        }
    }

    /// <summary>Paints the mask under the cursor (control pixels to image pixels through the aspect fit).</summary>
    private void PaintMaskAt(Vector2 pos)
    {
        Vector2 size = _maskView.Size;
        int iw = _mask.GetWidth(), ih = _mask.GetHeight();
        if (size.X <= 0 || size.Y <= 0 || iw <= 0 || ih <= 0)
        {
            return;
        }

        float scale = Math.Min(size.X / iw, size.Y / ih);
        Vector2 origin = (size - new Vector2(iw, ih) * scale) * 0.5f;
        float cx = (pos.X - origin.X) / scale, cy = (pos.Y - origin.Y) / scale;
        float radius = (float)_brush.Value;
        Color paint = _erase.ButtonPressed ? new Color(0, 0, 0, 0) : new Color(1, 1, 1, 1);
        for (int y = (int)(cy - radius); y <= cy + radius; y++)
        {
            for (int x = (int)(cx - radius); x <= cx + radius; x++)
            {
                if (x < 0 || y < 0 || x >= iw || y >= ih)
                {
                    continue;
                }

                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    _mask.SetPixel(x, y, paint);
                }
            }
        }

        Recombine();
        ShowWorkViews();
    }

    private string ResolveId()
    {
        string id = (_id.Text ?? "").Trim();
        if (id.Length > 0)
        {
            return id;
        }

        int n = _layers.Count + 1;
        while (_layers.Any(l => string.Equals(l.Id, $"layer_{n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }

        return $"layer_{n}";
    }

    /// <summary>Writes the decal PNG and files it as the layer's image (add or update).</summary>
    public string SaveDecalToLayer()
    {
        if (_decal == null || _decal.IsEmpty())
        {
            return "Nothing to save: capture, generate and extract first.";
        }

        string id = ResolveId();
        string sub = _kind.Selected == 1 ? "underlays" : "overlays";
        string dir = TerrainLayers.LayerDir();
        try
        {
            // Preflight the constructed output and every fixed save destination
            // before PNG, form or model mutation (including a proposed empty ID).
            string path = TerrainLayerPaths.Resolve(dir, $"{sub}/{id}.png", out string relative);
            if (!TerrainLayers.CanSave(dir, out string reason))
                return $"Could not save layer: {reason}";
            TerrainLayer next = ReadUi(id);
            next.Image = relative;
            var proposed = new List<TerrainLayer>(_layers);
            int index = proposed.FindIndex(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                proposed.Add(next);
            else
                proposed[index] = next;

            // Nested safe IDs remain valid. Recheck after parent creation and
            // immediately before SavePng; no transaction across PNG/JSON implied.
            string parent = relative.Substring(0, relative.LastIndexOf('/'));
            TerrainLayerPaths.CreateDirectory(dir, parent);
            path = TerrainLayerPaths.Resolve(dir, relative, out _);
            if (!TerrainLayers.CanSave(dir, out reason))
                return $"Could not save layer: {reason}";
            if (_decal.SavePng(path) != Error.Ok)
                return $"Could not write {path}.";
            if (!Persist(proposed))
                return "Could not write layers.json.";
            Select(id);
            return $"Saved {id}. -relayers in the client to see it.";
        }
        catch (Exception ex)
        {
            return $"Could not save layer: {ex.Message}";
        }
    }
}
#endif
