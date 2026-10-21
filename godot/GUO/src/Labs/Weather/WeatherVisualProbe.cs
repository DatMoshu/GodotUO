// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using Godot;
using GUO.Renderer;

namespace GUO.Labs.Weather;

// Synthetic render proof: uses production surface drawing, no proprietary art.
public partial class WeatherVisualProbe : Node2D
{
    private UltimaBatcher2D _batcher;
    public override async void _Ready()
    {
        GetWindow().Size = new Vector2I(800, 450);
        RenderingServer.SetDefaultClearColor(new Color("20262c"));
        var title = new Label { Text = "UO+ WEATHER  /  SURFACE RENDER PROBE", Position = new Vector2(28, 24) };
        AddChild(title);
        var note = new Label { Text = "Production puddle and ripple primitives on synthetic tiles\nNo shard or client artwork; wet-player integration requires a live scene.", Position = new Vector2(28, 370) };
        AddChild(note);
        var canvas = new Node2D();
        AddChild(canvas);
        _batcher = new UltimaBatcher2D(canvas.GetCanvasItem());
        _batcher.Begin();
        for (int i = 0; i < 4; i++)
        {
            var label = new Label { Text = new[] { "DRY", "FILLING", "FULL + RAIN", "WATER + RAIN" }[i], Position = new Vector2(35 + i * 195, 95) };
            AddChild(label);
            for (int tx = 0; tx < 3; tx++)
            for (int ty = 0; ty < 3; ty++)
            {
                int x = 65 + i * 195 + (tx - ty) * 22, y = 150 + (tx + ty) * 22;
                var texture = SolidColorTextureCache.GetTexture(i == 3 ? new GUO.Compat.Color(40, 73, 100, 255) : new GUO.Compat.Color(92, 89, 82, 255));
                for (int dy = -21; dy <= 21; dy++)
                {
                    int half = 22 - Math.Abs(dy);
                    _batcher.Draw(texture, new GUO.Compat.Rectangle(x + 22 - half, y + 22 + dy, half * 2, 1), null,
                        WeatherLab.Hue(1), 0, Vector2.Zero, SpriteEffects.None, 0);
                }
                var surface = new WeatherSurface(null) { Water = i == 3, Fill = i == 1 ? 0.4f : i == 2 ? 1 : 0,
                    RingAge = i >= 2 ? (tx + ty) % 3 * 0.25f + 0.1f : 1 };
                surface.Draw(_batcher, x, y, 0);
            }
        }
        _batcher.End();
        for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROOF");
        if (!string.IsNullOrWhiteSpace(output))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output)));
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(output) != Error.Ok) { GD.PrintErr("[weather visual] could not save PNG"); GetTree().Quit(1); return; }
            GD.Print("[weather visual] saved " + output);
            GetTree().Quit();
        }
    }
    public override void _ExitTree() => _batcher?.Dispose();
}
#endif
