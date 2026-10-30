// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Synthetic rendered mist proof; no live-world integration claim.</summary>
public partial class SpeedModeFogProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Use a real renderer for fog readback");
            GetWindow().Size = new Vector2I(1280, 720);
            var viewport = new SubViewport
            {
                Size = new Vector2I(1280, 720),
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            viewport.AddChild(new ColorRect { Size = viewport.Size, Color = new Color(0.12f, 0.12f, 0.12f) });
            var origin = new Vector2I(6000, 4000);
            using var surface = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            Fill(surface, origin);
            using var texture = ImageTexture.CreateFromImage(surface);
            var fog = new SpeedModeFogPass();
            viewport.AddChild(fog);
            var camera = new Vector3(6000, 4000, 0);
            void Bind(float density = 1, double seconds = 1, bool enabled = true)
                => fog.Bind(texture, viewport.Size, origin, camera, density, 0, 1, seconds, enabled);
            Bind(0);
            await Draw();
            using var before = viewport.GetTexture().GetImage();
            Bind();
            await Draw();
            using var after = viewport.GetTexture().GetImage();
            Require(after.GetPixel(400, 200).R > after.GetPixel(100, 200).R,
                "Distant floor must receive more mist than nearby floor");
            Require(after.GetPixel(400, 200).R > after.GetPixel(800, 200).R,
                "Height must reduce low mist");
            int mistPixels = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                bool changed = before.GetPixel(x, y) != after.GetPixel(x, y);
                if (y < 80 || x >= 960) Require(!changed, "Covered or missing surface must remain exact");
                else if (changed) mistPixels++;
            }
            Require(mistPixels > 100000, "Mist must affect exposed geometry");
            Bind();
            await Draw();
            using var repeat = viewport.GetTexture().GetImage();
            Require(Same(after, repeat), "Explicit time must produce the same mist");
            origin += new Vector2I(10, -10);
            Fill(surface, origin);
            texture.Update(surface);
            Bind();
            await Draw();
            using var rebased = viewport.GetTexture().GetImage();
            Require(Same(after, rebased), "Changing the surface origin must not move mist");
            Bind(0.25f);
            await Draw();
            using var faint = viewport.GetTexture().GetImage();
            Require(faint.GetPixel(400, 200).R < after.GetPixel(400, 200).R,
                "Quarter density must create fainter haze");
            Bind(seconds: 20);
            await Draw();
            using var drift = viewport.GetTexture().GetImage();
            Require(!Same(after, drift), "Wind-scrolled noise must move with time");
            Bind(enabled: false);
            await Draw();
            using var disabled = viewport.GetTexture().GetImage();
            Require(Same(before, disabled), "Fog toggle off must restore the frame exactly");
            fog.Bind(null, viewport.Size, origin, camera, 1, 100, 0, 0, true);
            await Draw();
            using var fallback = viewport.GetTexture().GetImage();
            Require(Same(before, fallback), "Missing surface must restore the frame exactly");
            string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                before.SavePng(Path.Combine(output, "guo_speed-weather_fog_before_20261029.png"));
                after.SavePng(Path.Combine(output, "guo_speed-weather_fog_after_20261029.png"));
            }
            GD.Print($"[speed-fog] PASS: {mistPixels} mist pixels, distance/height, covered/missing parity, quarter density, wind/time, origin invariance, toggle/fallback exact");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("[speed-fog] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    private static void Fill(Image image, Vector2I origin)
    {
        for (int y = 0; y < 720; y++)
        for (int x = 0; x < 1280; x++)
            SpeedModeRainProbe.WriteFixturePixel(image, x, y, 6000 + (x < 320 ? 2 : 12), 4000,
                x >= 960 ? 0 : y < 80 ? 17 : 1, origin, x >= 640 ? 36 : 0);
    }

    private async System.Threading.Tasks.Task Draw()
    {
        for (int i = 0; i < 2; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }

    private static bool Same(Image a, Image b) => a.GetData().AsSpan().SequenceEqual(b.GetData());
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
