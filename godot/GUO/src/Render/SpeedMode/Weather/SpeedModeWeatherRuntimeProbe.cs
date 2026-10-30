// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;
using GUO.Configuration;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Synthetic production-callback proof; no shard or client art.</summary>
public partial class SpeedModeWeatherRuntimeProbe : Node
{
    public override async void _Ready()
    {
        RenderTarget2D target = null;
        UltimaBatcher2D batcher = null;
        SpeedModeWeatherRuntime runtime = null;
        var owner = new object();
        uint tick = Time.Ticks;
        try
        {
            Require(DisplayServer.GetName() != "headless", "Use a real renderer for runtime readback");
            GetWindow().Size = new Vector2I(1280, 720);
            target = new RenderTarget2D(this, 1280, 720);
            using var surface = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            using var colors = Image.CreateEmpty(1280, 720, false, Image.Format.Rgba8);
            using var light = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            var origin = new Vector2I(6000, 4000);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                bool covered = x >= 320 && x < 960 && y >= 180 && y < 540;
                SpeedModeRainProbe.WriteFixturePixel(surface, x, y, 6000 + x / 44f, 4000 + y / 44f,
                    covered ? 49 : 33, origin);
                colors.SetPixel(x, y, new Color(0.3f, 0.25f, 0.2f));
                light.SetPixel(x, y, new Color(0.1f, 0.15f, 0.2f));
            }
            using var surfaceTexture = ImageTexture.CreateFromImage(surface);
            using var colorTexture = ImageTexture.CreateFromImage(colors);
            using var lightTexture = ImageTexture.CreateFromImage(light);
            target.ViewportNode.AddChild(new TextureRect { Texture = colorTexture, Size = new Vector2(1280, 720), ZIndex = -10 });
            batcher = new UltimaBatcher2D(target.CanvasItem);
            runtime = new SpeedModeWeatherRuntime();
            runtime.Initialize(target);
            target.ViewportNode.AddChild(runtime);
            var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
            var profile = new Profile { LightingSpeedMode = true, SpeedModeReflections = false };
            var state = new SpeedModeWeather();
            state.Advance(WeatherType.WT_RAIN, 70, 0, 0, 60);
            var view = new Transform2D(0, new Vector2(17, 11));
            UltimaBatcher2D.WeatherLines DrawClassic(Color color)
            {
                batcher.BeginFrame();
                batcher.SetRenderTarget(target);
                batcher.Begin(view);
                var ticket = batcher.BeginSpeedWeatherLines();
                RenderingServer.CanvasItemAddRect(ticket.Item, new Rect2(-7, -1, 20, 20), color);
                batcher.EndSpeedWeatherLines();
                batcher.End(); batcher.SetRenderTarget(null);
                return ticket;
            }
            void Arm(UltimaBatcher2D.WeatherLines ticket) => runtime.Arm(world, profile, state, batcher,
                ticket, surfaceTexture, colorTexture, view, new Vector3(6000, 4000, 0), 1, 0.123);
            void Surface() => SpeedModeBindings.PublishSurface(owner, surfaceTexture, origin, new Vector2I(1280, 720));
            void Light() => SpeedModeBindings.PublishLight(owner, lightTexture, new Vector4(0, 0, 1280, 720), Vector3.Zero);
            var classic = DrawClassic(Colors.White);
            Arm(classic);
            Surface();
            await Draw();
            using var before = target.Texture.GetImage();
            Require(!runtime.Bound && before.GetPixel(15, 15).R > 0.95f, "Surface-only frame preserves classic lines");
            Light();
            Require(runtime.Bound && runtime.BoundSequence == SpeedModeBindings.Current.Sequence,
                "Light publication must synchronously elect the matching weather frame");
            await PostDraw();
            using var after = target.Texture.GetImage();
            Require(after.GetPixel(15, 15).R < 0.9f, "Valid speed frame hides classic lines before canvas draws");
            int changed = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                bool different = before.GetPixel(x, y) != after.GetPixel(x, y);
                if (x >= 320 && x < 960 && y >= 180 && y < 540)
                    Require(!different, "Native overlays must preserve the covered rectangle exactly");
                else if (different) changed++;
            }
            Require(changed > 100000, "Native world overlays must visibly render");
            SpeedModeBindings.Clear(owner);
            Require(!runtime.Bound, "Invalidation clears native overlays immediately");
            await PostDraw();
            using var off = target.Texture.GetImage();
            Require(Same(before, off), "Core abort/clear restores classic weather and pixels exactly");
            Arm(classic); Surface();
            Time.Ticks++;
            Light();
            Require(!runtime.Bound, "Delayed light must not use an old world snapshot");
            Time.Ticks = tick;
            runtime.ResetFrame();
            var fresh = DrawClassic(Colors.Green);
            batcher.ShowSpeedWeatherLines(classic, false);
            await PostDraw();
            using var reused = target.Texture.GetImage();
            Require(reused.GetPixel(15, 15).G > 0.95f, "Old visibility ticket cannot hide a reused pooled item");
            Arm(fresh); Surface(); Light();
            Require(runtime.Bound, "Fresh ticket can bind after fallback");
            profile.LightingSpeedMode = false;
            Light();
            Require(!runtime.Bound, "Main option off rejects the publication");
            await PostDraw();
            using var disabled = target.Texture.GetImage();
            Require(Same(reused, disabled), "Main off restores the complete classic frame exactly");
            string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                before.SavePng(Path.Combine(output, "guo_speed-weather_runtime_before_20261029.png"));
                after.SavePng(Path.Combine(output, "guo_speed-weather_runtime_after_20261029.png"));
            }
            GD.Print($"[speed-weather-runtime] PASS: {changed} changed pixels; publish/invalidate, native canvas before UI, camera transform, covered parity, classic-line election, stale tick, pooled-ticket isolation and exact main-off restoration");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("[speed-weather-runtime] FAIL: " + error);
            GetTree().Quit(1);
        }
        finally
        {
            Time.Ticks = tick;
            SpeedModeBindings.Clear(owner);
            if (runtime != null) { runtime.Release(); runtime.GetParent()?.RemoveChild(runtime); runtime.Free(); }
            batcher?.Dispose(); target?.Dispose();
        }
    }

    private async System.Threading.Tasks.Task Draw()
    {
        for (int i = 0; i < 2; i++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await PostDraw(); }
    }
    private async System.Threading.Tasks.Task PostDraw()
        => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private static bool Same(Image a, Image b) => a.GetData().AsSpan().SequenceEqual(b.GetData());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
