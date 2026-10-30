// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>
/// Published-metadata fixture only. Does not manufacture a native bank witness
/// or prove that a hidden physical roof produced the correct surface flags.
/// </summary>
public partial class WeatherContainmentProbe : Node
{
    private static readonly Vector2I Size = new(1280, 720);
    // Covered floor, furniture, actor, porch, lower floor below upper deck;
    // exposed upper deck/paving, exposed non-puddle floor, uninitialized sample.
    private static readonly int[] Packed = { 49, 20, 20, 49, 49, 33, 1, 0 };
    private SubViewport _view;
    private SpeedModeRainPass _rain;
    private SpeedModeWetPass _wet;
    private ImageTexture _surface, _lights;
    private readonly Vector2I _origin = new(1500, 1500);
    private string _output;

    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Use a real renderer for containment readback");
            _output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_CONTAINMENT_OUT");
            Require(!string.IsNullOrWhiteSpace(_output), "Set GUO_WEATHER_CONTAINMENT_OUT to external evidence stills");
            Directory.CreateDirectory(_output);
            GetWindow().Size = Size;
            _view = new SubViewport {
                Size = Size, TransparentBg = true,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(_view);
            _rain = new SpeedModeRainPass();
            _wet = new SpeedModeWetPass();
            _view.AddChild(_wet); _view.AddChild(_rain);
            using var field = Image.CreateEmpty(Size.X, Size.Y, false, Image.Format.Rgbah);
            Fill(field, _origin);
            _surface = ImageTexture.CreateFromImage(field);
            using var light = Image.CreateEmpty(Size.X, Size.Y, false, Image.Format.Rgbah);
            light.Fill(new Color(0, 0, 0));
            _lights = ImageTexture.CreateFromImage(light);

            long rainFull = 0, rainHalf = 0, wetFull = 0, wetHalf = 0;
            foreach (double time in new[] { 0.137, 0.421, 0.799, 1.173 })
            {
                // Both streaks and screen-offset runoff must also respect
                // covered/unknown pixels, not just ground splash admission.
                using (var all = await Rain(time, 1, 7)) CheckProtected(all, true);
                using var full = await Rain(time, 1);
                using var keep60 = await Rain(time, 0.6f);
                using var half = await Rain(time, 0.5f);
                using var keep40 = await Rain(time, 0.4f);
                CheckProtected(full, true);
                CheckNested(full, keep60, half, keep40, null, false);
                rainFull += Changed(full, null); rainHalf += Changed(half, null);

                using var dryRings = await Wet(time, 0, 0);
                using var wetAll = await Wet(time, 1, 1);
                using var wet60 = await Wet(time, 0.6f, 1);
                using var wet50 = await Wet(time, 0.5f, 1);
                using var wet40 = await Wet(time, 0.4f, 1);
                CheckProtected(wetAll, true);
                CheckNested(wetAll, wet60, wet50, wet40, dryRings, true);
                wetFull += Changed(wetAll, dryRings); wetHalf += Changed(wet50, dryRings);

                if (time == 0.421)
                {
                    full.SavePng(Path.Combine(_output, "rain-full.png"));
                    half.SavePng(Path.Combine(_output, "rain-half.png"));
                    wetAll.SavePng(Path.Combine(_output, "wet-full.png"));
                    wet50.SavePng(Path.Combine(_output, "wet-half.png"));
                    // Rebase the metadata, retaining identical absolute world cells.
                    var shifted = _origin + new Vector2I(10, -10);
                    Fill(field, shifted); _surface.Update(field);
                    using var rebased = await Rain(time, 0.5f, 2, shifted);
                    Require(Same(half, rebased), "Origin rebase moved rain events");
                    using var wetRebased = await Wet(time, 0.5f, 1, shifted);
                    Require(Same(wet50, wetRebased), "Origin rebase moved wet events");
                    Fill(field, _origin); _surface.Update(field);
                    using var repeat = await Rain(time, 0.5f);
                    Require(Same(half, repeat), "Frame scheduling changed explicit-time rain");
                    using var wetRepeat = await Wet(time, 0.5f, 1);
                    Require(Same(wet50, wetRepeat), "Frame scheduling changed explicit-time wet rings");
                }
            }
            Require(rainHalf > 0 && rainHalf < rainFull, "Rain density gate needs positive and removed splashes");
            Require(wetHalf > 0 && wetHalf < wetFull, "Wet density gate needs positive and removed rings");
            await CheckEventStarts();
            _wet.HideForFallback();
            _rain.Bind(_surface, Size, 0, 0, 1, surfaceOrigin: _origin);
            using (var zero = await Capture()) Require(Changed(zero, null) == 0, "Rain at zero intensity");
            _rain.HideForFallback();
            using (var off = await Capture()) Require(Changed(off, null) == 0, "Fallback rain retained pixels");
            SpeedModeRainProbe.CheckOwnership(_rain, _surface, Size);
            GD.Print($"[weather-containment] PASS fixture only: covered/unknown=0; rain pixels {rainFull}->{rainHalf}; wet-ring pixels {wetFull}->{wetHalf}; 40/50/60 reductions nested, rebase/repeat/off. Pixel counts are NOT event-start rates or native shelter proof.");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("[weather-containment] FAIL: " + error);
            GetTree().Quit(1);
        }
        finally
        {
            _rain?.HideForFallback(); _wet?.HideForFallback();
            _surface?.Dispose(); _lights?.Dispose();
        }
    }

    private static void Fill(Image field, Vector2I origin)
    {
        for (int y = 0; y < Size.Y; y++)
        for (int x = 0; x < Size.X; x++)
        {
            int zone = x / (Size.X / Packed.Length);
            // Quantized relative coordinates fit RGBA16F exactly; upper deck
            // remains exposed despite the adjacent protected lower-floor zone.
            SpeedModeRainProbe.WriteFixturePixel(field, x, y,
                1500 + MathF.Round(x / 44f * 16) / 16,
                1500 + MathF.Round(y / 44f * 16) / 16,
                Packed[zone], origin, zone == 5 ? 40 : 0);
        }
    }

    private async Task<Image> Rain(double time, float keep, int mask = 2, Vector2I? origin = null)
    {
        _wet.HideForFallback();
        _rain.Bind(_surface, Size, 1, 0, time, surfaceOrigin: origin ?? _origin);
        var material = (ShaderMaterial)_rain.Material;
        material.SetShaderParameter("component_mask", mask);
        material.SetShaderParameter("splash_start_fraction", keep);
        return await Capture();
    }

    private async Task<Image> Wet(double time, float keep, float intensity, Vector2I? origin = null)
    {
        _rain.HideForFallback();
        _wet.Bind(_surface, _lights, Size, origin ?? _origin, 1, intensity, time);
        ((ShaderMaterial)_wet.Material).SetShaderParameter("splash_start_fraction", keep);
        return await Capture();
    }

    private async Task<Image> Capture()
    {
        for (int i = 0; i < 2; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        return _view.GetTexture().GetImage();
    }

    private async Task CheckEventStarts()
    {
        _rain.HideForFallback(); _wet.HideForFallback();
        // Invoke the production include rather than copying its hash. One
        // texel is one candidate start: 32x32 world cells, 64 event indices.
        using var shader = new Shader { Code = """
            shader_type canvas_item;
            render_mode unshaded;
            #include "res://src/Render/SpeedMode/Weather/speed_weather_events.gdshaderinc"
            uniform uint event_stream = 2u;
            void fragment() {
                vec2 pixel = floor(UV * vec2(1280.0, 720.0));
                int sample_index = int(pixel.y) * 1280 + int(pixel.x);
                if (sample_index >= 65536) { COLOR = vec4(0.0); }
                else {
                    int event_index = sample_index % 64;
                    int cell_index = sample_index / 64;
                    vec2 cell = vec2(float(2990 + cell_index / 32), float(2990 + cell_index % 32));
                    float h = weather_event_hash(cell, float(event_index), event_stream);
                    COLOR = vec4(h < 0.6 ? 1.0 : 0.0, h < 0.5 ? 1.0 : 0.0, h < 0.4 ? 1.0 : 0.0, 1.0);
                }
            }
            """ };
        using var material = new ShaderMaterial { Shader = shader };
        var diagnostic = new ColorRect { Size = Size, Material = material };
        _view.AddChild(diagnostic);
        try
        {
            foreach (int stream in new[] { 2, 4 })
            {
                material.SetShaderParameter("event_stream", stream);
                using var image = await Capture();
                int[] counts = new int[3];
                bool admitted = false, rejected = false;
                for (int sample = 0; sample < 65536; sample++)
                {
                    Color pixel = image.GetPixel(sample % Size.X, sample / Size.X);
                    if (pixel.R > 0.5f) counts[0]++;
                    if (pixel.G > 0.5f) counts[1]++;
                    if (pixel.B > 0.5f) counts[2]++;
                    Require(pixel.R >= pixel.G && pixel.G >= pixel.B, "Non-nested GPU event gate");
                    if (sample < 64) { admitted |= pixel.G > 0.5f; rejected |= pixel.G < 0.5f; }
                }
                Require(admitted && rejected, "Fixed world cell must vary admission across events");
                Require(Math.Abs(counts[0] / 65536.0 - 0.6) < 0.01
                    && Math.Abs(counts[1] / 65536.0 - 0.5) < 0.01
                    && Math.Abs(counts[2] / 65536.0 - 0.4) < 0.01, "GPU candidate-start fractions outside tolerance");
                GD.Print($"[weather-containment] GPU candidate starts stream{stream}: {string.Join(",", counts)}/65536 (retention60/50/40)");
            }
        }
        finally
        {
            _view.RemoveChild(diagnostic);
            diagnostic.Material = null;
            diagnostic.Free();
        }
    }

    private static bool Same(Image a, Image b) => a.GetData().AsSpan().SequenceEqual(b.GetData());
    private static bool Different(Color a, Color b) => a != b;
    private static void CheckProtected(Image image, bool unknown)
    {
        for (int y = 0; y < Size.Y; y++)
        for (int x = 0; x < Size.X; x++)
            if (x < 800 || unknown && x >= 1120)
                Require(image.GetPixel(x, y).A == 0, $"Weather at protected/unknown pixel {x},{y}");
    }

    private static long Changed(Image image, Image baseline)
    {
        long count = 0;
        for (int y = 0; y < Size.Y; y++)
        for (int x = 0; x < Size.X; x++)
            if (baseline == null ? image.GetPixel(x, y).A > 0 : Different(image.GetPixel(x, y), baseline.GetPixel(x, y))) count++;
        return count;
    }

    private static void CheckNested(Image full, Image keep60, Image half, Image keep40, Image baseline, bool wet)
    {
        for (int y = 0; y < Size.Y; y++)
        for (int x = 0; x < Size.X; x++)
        {
            Color zero = baseline?.GetPixel(x, y) ?? Colors.Transparent;
            bool a = wet ? Different(full.GetPixel(x, y), zero) : full.GetPixel(x, y).A > 0;
            bool b = wet ? Different(keep60.GetPixel(x, y), zero) : keep60.GetPixel(x, y).A > 0;
            bool c = wet ? Different(half.GetPixel(x, y), zero) : half.GetPixel(x, y).A > 0;
            bool d = wet ? Different(keep40.GetPixel(x, y), zero) : keep40.GetPixel(x, y).A > 0;
            Require((!d || c) && (!c || b) && (!b || a), "Start-density alternatives must be nested");
            if (wet && (a || b || c || d)) Require(x >= 800 && x < 960, "Ring escaped exposed paving allowlist");
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
