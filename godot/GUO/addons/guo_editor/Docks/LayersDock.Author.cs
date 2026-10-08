#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using GUO.Renderer;

/// <summary>
/// The Layers dock's authoring side: capture the World tab's view of the
/// rect, repaint it through ComfyUI img2img, subtract the capture to get the
/// decal, erase strays with a mask brush, and save it as the layer's image.
/// One quad, one image: no tiling anywhere in this path.
/// </summary>
public partial class LayersDock
{
    private OptionButton _wfPick, _viewPick;
    private SpinBox _tol, _feather, _brush;
    private TextureRect _workPreview;
    private Image _capture, _generated, _decal;
    private bool _busy, _brushing;

    private void BuildAuthorUi(VBoxContainer root)
    {
        var wf = new HBoxContainer();
        root.AddChild(wf);
        wf.AddChild(new Label { Text = "Workflow" });
        _wfPick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        wf.AddChild(_wfPick);
        var refresh = new Button { Text = "Refresh" };
        refresh.Pressed += RefreshAuthorWorkflows;
        wf.AddChild(refresh);
        RefreshAuthorWorkflows();

        var go = new HBoxContainer();
        root.AddChild(go);
        var capture = new Button { Text = "Capture area" };
        capture.Pressed += CaptureFromUi;
        go.AddChild(capture);
        var generate = new Button { Text = "Generate" };
        generate.Pressed += GenerateFromUi;
        go.AddChild(generate);

        var ex = new HBoxContainer();
        root.AddChild(ex);
        ex.AddChild(new Label { Text = "Tolerance" });
        _tol = new SpinBox { MinValue = 0, MaxValue = 255, Step = 1, Value = 12 };
        ex.AddChild(_tol);
        ex.AddChild(new Label { Text = "Feather" });
        _feather = new SpinBox { MinValue = 0, MaxValue = 64, Step = 1, Value = 2 };
        ex.AddChild(_feather);
        var reextract = new Button { Text = "Re-extract" };
        reextract.Pressed += () => { Reextract(); ShowWorkView(); };
        ex.AddChild(reextract);

        var vw = new HBoxContainer();
        root.AddChild(vw);
        _viewPick = new OptionButton();
        _viewPick.AddItem("Decal");
        _viewPick.AddItem("Capture");
        _viewPick.AddItem("Generated");
        _viewPick.ItemSelected += _ => ShowWorkView();
        vw.AddChild(_viewPick);
        vw.AddChild(new Label { Text = "Brush" });
        _brush = new SpinBox { MinValue = 2, MaxValue = 128, Step = 2, Value = 16 };
        vw.AddChild(_brush);
        var save = new Button { Text = "Save to layer" };
        save.Pressed += () => Report(SaveDecalToLayer());
        vw.AddChild(save);

        _workPreview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 180),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _workPreview.GuiInput += OnBrushInput;
        root.AddChild(_workPreview);
    }

    private void RefreshAuthorWorkflows()
    {
        if (_wfPick == null)
        {
            return;
        }

        _wfPick.Clear();
        foreach (string f in ComfyUiProvider.Workflows(ArtDock.WorkflowFolder))
        {
            _wfPick.AddItem(Path.GetFileName(f));
            _wfPick.SetItemMetadata(_wfPick.ItemCount - 1, f);
            if (string.Equals(Path.GetFileName(f), "studio_img2img.json", StringComparison.OrdinalIgnoreCase))
            {
                _wfPick.Selected = _wfPick.ItemCount - 1;
            }
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
    }

    private void CurrentRect(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Min((int)_x0.Value, (int)_x1.Value);
        x1 = Math.Max((int)_x0.Value, (int)_x1.Value);
        y0 = Math.Min((int)_y0.Value, (int)_y1.Value);
        y1 = Math.Max((int)_y0.Value, (int)_y1.Value);
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
        if (x1 < x0 || y1 < y0)
        {
            Report("Empty rect: set X0..X1 and Y0..Y1 first.");
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        int w = Math.Clamp((x1 - x0 + 1) * 64, 256, 1024);
        int h = Math.Clamp((y1 - y0 + 1) * 64, 256, 1024);
        int spanX = Math.Max(1, x1 - x0 + 1), spanY = Math.Max(1, y1 - y0 + 1);
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

        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                float u = (i + 0.5f) / w, v = (j + 0.5f) / h;
                float tx = x0 + 0.5f + u * spanX, ty = y0 - 0.5f + v * spanY;
                float px = (tx - ty) * 22f - 22f, py = (tx + ty) * 22f - (ztop << 2) - 22f;
                var s = cam.WorldToScreen(new GUO.Compat.Point((int)Math.Round(px), (int)Math.Round(py)));
                img.SetPixel(i, j, Bilinear(shot, s.X, s.Y));
            }
        }

        _capture?.Dispose();
        _capture = img;
        _generated?.Dispose();
        _generated = null;
        _decal?.Dispose();
        _decal = null;
        _viewPick.Selected = 1;
        ShowWorkView();
        Report($"Captured {w}x{h} over ({x0},{y0})-({x1},{y1}).");
    }

    private static Color Bilinear(Image shot, float x, float y)
    {
        int w = shot.GetWidth(), h = shot.GetHeight();
        int x0 = Math.Clamp((int)Math.Floor(x), 0, w - 1), x1 = Math.Min(x0 + 1, w - 1);
        int y0 = Math.Clamp((int)Math.Floor(y), 0, h - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = Math.Clamp(x - (int)Math.Floor(x), 0f, 1f), fy = Math.Clamp(y - (int)Math.Floor(y), 0f, 1f);
        Color a = shot.GetPixel(x0, y0), b = shot.GetPixel(x1, y0);
        Color c = shot.GetPixel(x0, y1), d = shot.GetPixel(x1, y1);
        return new Color(
            a.R + (b.R - a.R) * fx + (c.R - a.R) * fy + (a.R - b.R - c.R + d.R) * fx * fy,
            a.G + (b.G - a.G) * fx + (c.G - a.G) * fy + (a.G - b.G - c.G + d.G) * fx * fy,
            a.B + (b.B - a.B) * fx + (c.B - a.B) * fy + (a.B - b.B - c.B + d.B) * fx * fy,
            a.A + (b.A - a.A) * fx + (c.A - a.A) * fy + (a.A - b.A - c.A + d.A) * fx * fy);
    }

    private async void GenerateFromUi()
    {
        if (_busy)
        {
            return;
        }

        if (_capture == null)
        {
            Report("Capture the area first.");
            return;
        }

        string wf = _wfPick.ItemCount > 0 && !_wfPick.Disabled ? (string)_wfPick.GetItemMetadata(_wfPick.Selected) : "";
        if (string.IsNullOrEmpty(wf))
        {
            Report("Pick an img2img workflow first.");
            return;
        }

        _busy = true;
        try
        {
            string url = EditorData.Setting("UO_COMFY_URL", "http://127.0.0.1:8188");
            Report($"Generating on {url}...");
            using var provider = new ComfyUiProvider(url);
            var req = new ImageRequest
            {
                Prompt = _prompt.Text ?? "",
                WorkflowPath = wf,
                InputPng = _capture.SavePngToBuffer(),
                InputName = $"layer:{(_id.Text ?? "").Trim()}",
                Width = _capture.GetWidth(),
                Height = _capture.GetHeight(),
            };
            ImageResult r = await provider.RunAsync(req, null, CancellationToken.None);
            if (r.Error != null)
            {
                Report(r.Error);
                return;
            }

            if (r.Pngs.Count == 0)
            {
                Report("ComfyUI returned no images.");
                return;
            }

            var gen = new Image();
            if (gen.LoadPngFromBuffer(r.Pngs[0]) != Error.Ok || gen.IsEmpty())
            {
                gen.Dispose();
                Report("ComfyUI returned an undecodable image.");
                return;
            }

            if (gen.GetWidth() != _capture.GetWidth() || gen.GetHeight() != _capture.GetHeight())
            {
                gen.Resize(_capture.GetWidth(), _capture.GetHeight());
            }

            _generated?.Dispose();
            _generated = gen;
            Reextract();
            _viewPick.Selected = 0;
            ShowWorkView();
            Report($"{r.Pngs.Count} image(s), seed {r.Seed}; decal extracted. Erase strays with the brush, then Save to layer.");
        }
        finally
        {
            _busy = false;
        }
    }

    private void Reextract()
    {
        _decal?.Dispose();
        _decal = null;
        if (_capture == null || _generated == null)
        {
            return;
        }

        _decal = TerrainLayers.ExtractDecal(_capture, _generated, (float)_tol.Value, (float)_feather.Value);
    }

    private void ShowWorkView()
    {
        if (_workPreview == null)
        {
            return;
        }

        Image img = _viewPick.Selected == 1 ? _capture : _viewPick.Selected == 2 ? _generated : _decal;
        _workPreview.Texture = img == null || img.IsEmpty() ? null : ImageTexture.CreateFromImage(img);
    }

    private void OnBrushInput(InputEvent e)
    {
        if (_decal == null || _decal.IsEmpty())
        {
            return;
        }

        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            _brushing = mb.Pressed;
            if (_brushing)
            {
                _viewPick.Selected = 0;
                EraseAt(mb.Position);
            }
        }
        else if (e is InputEventMouseMotion mm && _brushing && mm.ButtonMask.HasFlag(MouseButtonMask.Left))
        {
            EraseAt(mm.Position);
        }
    }

    /// <summary>Erases the decal under the cursor (control pixels to image pixels through the aspect fit).</summary>
    private void EraseAt(Vector2 pos)
    {
        Vector2 size = _workPreview.Size;
        int iw = _decal.GetWidth(), ih = _decal.GetHeight();
        if (size.X <= 0 || size.Y <= 0 || iw <= 0 || ih <= 0)
        {
            return;
        }

        float scale = Math.Min(size.X / iw, size.Y / ih);
        Vector2 origin = (size - new Vector2(iw, ih) * scale) * 0.5f;
        float cx = (pos.X - origin.X) / scale, cy = (pos.Y - origin.Y) / scale;
        float radius = (float)_brush.Value;
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
                    _decal.SetPixel(x, y, new Color(0, 0, 0, 0));
                }
            }
        }

        ShowWorkView();
    }

    private string ResolveId()
    {
        string id = (_id.Text ?? "").Trim();
        if (id.Length > 0)
        {
            return id;
        }

        Refresh();
        int n = _layers.Count + 1;
        while (_layers.Any(l => string.Equals(l.Id, $"layer_{n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }

        _id.Text = $"layer_{n}";
        return _id.Text;
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
        Directory.CreateDirectory(Path.Combine(dir, sub));
        string path = Path.Combine(dir, sub, $"{id}.png");
        if (_decal.SavePng(path) != Error.Ok)
        {
            return $"Could not write {path}.";
        }

        _image.Text = $"{sub}/{id}.png";
        TerrainLayer existing = _layers.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
        return existing == null ? AddFromUi() : UpdateSelected();
    }
}
#endif
