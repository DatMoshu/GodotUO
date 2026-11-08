// SPDX-License-Identifier: BSD-2-Clause
// Task1057 original solid-color GPU alpha and pooled-borrow lifetime controls.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Renderer.SpeedMode.Weather;

public partial class CloudCompositeContractProbe : Node
{
    private RenderTargets _targets;
    private RenderTarget2D _screen, _cloud;
    private UltimaBatcher2D _batcher;
    private ColorRect _ground, _cloudColor;
    private string _out;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private static readonly Rectangle Full = new(0, 0, 1280, 720);
    private static readonly Color Ink = new(.8f, .4f, .2f, 1);
    private static object Read(object value, string field) => value.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    private async Task Painted() => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private static float Error(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    private static float[] Rgba(Color c) => new[] { c.R, c.G, c.B, c.A };
    private Color Ground => _ground.Color;

    private void CreateCloud()
    {
        _cloud = new RenderTarget2D(this, 1280, 720);
        _cloudColor = new ColorRect { Size = new Vector2(1280, 720), Color = Ink };
        _cloud.ViewportNode.AddChild(_cloudColor);
    }

    private void Submit(object owner, long generation, bool enabled = true, bool wrongBlend = false, bool abort = false)
    {
        _targets.BeginCloudFrame(owner, _batcher);
        _batcher.BeginFrame(deferRetainedHiding: true);
        bool complete = false;
        try
        {
            if (wrongBlend)
            {
                // Actual GPU wrong-blend negative control, with same viewport texture.
                _batcher.Begin();
                _batcher.Draw(_targets.WorldRenderTarget.Texture, Full, new Vector3(0, 0, 1), 0);
                _batcher.SetBlendState(new BlendState { ColorSourceBlend = Blend.SourceAlpha,
                    ColorDestinationBlend = Blend.InverseSourceAlpha,
                    AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha });
                _batcher.Draw(_cloud.Texture, Full, new Vector3(0, 0, 1), 0);
                _batcher.SetBlendState(null); _batcher.End();
            }
            else
            {
                if (enabled) Check(_targets.PublishCloud(owner, _targets.CloudFrame,
                    _targets.WorldRenderTarget, _cloud, generation), "matching publication rejected");
                _targets.Draw(_batcher);
            }
            complete = !abort;
        }
        finally
        {
            if (!complete) _targets.ClearCloudFrame();
            _batcher.EndFrame(complete);
        }
    }

    private async Task<Color> Output(string name)
    {
        await Painted();
        using var image = _screen.Texture.GetImage();
        Check(image.SavePng(Path.Combine(_out, "stills", name + ".png")) == Godot.Error.Ok, "save failed");
        return image.GetPixel(640, 360);
    }

    private async Task Alpha(object owner)
    {
        foreach (float alpha in new[] { 0f, .5f, 1f })
        foreach (float ground in new[] { .15f, .8f })
        {
            _ground.Color = new Color(ground, ground, ground, 1);
            _cloudColor.Color = new Color(Ink.R, Ink.G, Ink.B, alpha);
            // Newly allocated sibling viewports need painted dependency frames.
            // Do not treat a cold all-transparent first read as blend evidence.
            for (int warm = 0; warm < 3; warm++) { Submit(owner, 1); await Painted(); }
            Submit(owner, 1);
            var final = await Output($"alpha-{alpha:0.0}-ground-{ground:0.0}");
            using var rawImage = _cloud.Texture.GetImage();
            using var groundImage = _targets.WorldRenderTarget.Texture.GetImage();
            Check(Error(groundImage.GetPixel(640, 360), Ground) <= 2f / 255,
                "actual ground viewport not ready");
            var raw = rawImage.GetPixel(640, 360);
            var expected = new Color(raw.R + (1 - raw.A) * ground,
                raw.G + (1 - raw.A) * ground, raw.B + (1 - raw.A) * ground, 1);
            float error = Error(final, expected);
            _rows.Add(new { test = "actual-alpha", alpha, ground, raw = Rgba(raw), final = Rgba(final),
                expected = Rgba(expected), error }); Save();
            Check(error <= 3f / 255, "actual premultiplied equation failed");
            if (alpha == .5f)
            {
                Submit(owner, 1, wrongBlend: true);
                var wrong = await Output($"wrong-alpha-ground-{ground:0.0}");
                float wrongError = Error(wrong, expected);
                _rows.Add(new { test = "actual-wrong-blend", ground, final = Rgba(wrong), wrongError }); Save();
                Check(wrongError > .08f, "wrong-blend negative control did not fail");
            }
        }
    }

    private async Task Lifetime(object owner)
    {
        foreach (bool retained in new[] { false, true })
        {
            UltimaBatcher2D.RetainCanvas = retained;
            _ground.Color = new Color(.2f, .2f, .2f, 1); _cloudColor.Color = new Color(.8f, .4f, .2f, .5f);
            Submit(owner, 1); await Output("lifetime-active-" + retained);
            Check(ReferenceEquals(Read(_batcher, "_cloudDrawOwner"), owner), "active borrower missing");
            int first = (int)Read(_batcher, "_cloudFirstItem"), end = (int)Read(_batcher, "_cloudEndItem");
            Check(end > first, "no actual tracked cloud commands");
            _targets.ClearCloudFrame();
            Check(Read(_batcher, "_cloudDrawTarget") == null, "early-return borrow retained");
            var clear = await Output("lifetime-clear-" + retained);
            Check(Error(clear, Ground) <= 2f / 255, "cleared native/retained commands still draw cloud");
            Submit(owner, 1, enabled: false);
            Check(Error(await Output("lifetime-off-" + retained), Ground) <= 2f / 255, "off output changed");
            Submit(owner, 1, abort: true); await Painted();
            Check(Read(_batcher, "_cloudDrawTarget") == null, "abort retained cloud target");
            Submit(owner, 1); await Output("lifetime-after-abort-" + retained);
            _rows.Add(new { test = "borrow-lifecycle", retained, trackedItems = end - first,
                clear = true, off = true, abort = true }); Save();
        }
        var oldCloud = _cloud;
        Submit(owner, 1); // deliberately no FramePostDraw before unload
        _targets.RevokeCloud(owner, oldCloud, 1);
        oldCloud.Dispose(); await Painted();
        Check(Read(_batcher, "_cloudDrawTarget") == null, "queued unload retained freed target");
        CreateCloud(); object next = new();
        Submit(next, 2); await Output("new-owner");
        _targets.RevokeCloud(owner, oldCloud, 1);
        Check(ReferenceEquals(Read(_batcher, "_cloudDrawOwner"), next), "old owner cleared new borrower");
        _targets.EnsureSizes(this, Full, new Rectangle(0, 0, 960, 640), 1);
        Check(Read(_batcher, "_cloudDrawTarget") == null, "resize retained old borrow");
        Check(oldCloud.IsDisposed, "old target not disposed");
        _rows.Add(new { test = "queued-unload-new-owner-resize", queued = true, oldReleaseGuard = true, resized = true }); Save();
    }

    private void Save() => File.WriteAllText(Path.Combine(_out, "data", "contract-results.json"),
        JsonSerializer.Serialize(new { rows = _rows, failures = _failures }, new JsonSerializerOptions { WriteIndented = true }));

    public override async void _Ready()
    {
        bool retained = UltimaBatcher2D.RetainCanvas;
        try
        {
            Check(DisplayServer.GetName() != "headless", "real renderer required");
            _out = System.Environment.GetEnvironmentVariable("GUO_CLOUD_COMPOSITE_OUT");
            Check(!string.IsNullOrWhiteSpace(_out) && Path.IsPathRooted(_out), "external output required");
            Directory.CreateDirectory(Path.Combine(_out, "data")); Directory.CreateDirectory(Path.Combine(_out, "stills"));
            GetWindow().Size = new Vector2I(1280, 720);
            _screen = new RenderTarget2D(this, 1280, 720);
            _targets = new RenderTargets(); _targets.EnsureSizes(this, Full, Full, 1);
            _targets.SetBackgroundReplaced(() => true);
            _ground = new ColorRect { Size = new Vector2(1280, 720), Color = Colors.White };
            _targets.WorldRenderTarget.ViewportNode.AddChild(_ground);
            _batcher = new UltimaBatcher2D(_screen.CanvasItem);
            CreateCloud(); object owner = new();
            CloudPublicationStateRegression.Run();
            await Alpha(owner); await Lifetime(owner);
            GD.Print("[cloud-contract] PASS actual-alpha0/.5/1 wrongblend native+retained clear/off/abort/queuedunload/newowner/resize");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            _failures.Add(error.ToString()); if (_out != null && Directory.Exists(Path.Combine(_out, "data"))) Save();
            GD.PrintErr("[cloud-contract] FAIL " + error); GetTree().Quit(1);
        }
        finally
        {
            _targets?.ClearCloudFrame(); _cloud?.Dispose(); _batcher?.Dispose();
            _targets?.Dispose(); _screen?.Dispose(); UltimaBatcher2D.RetainCanvas = retained;
        }
    }
}
