// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Rendered RGBA16F fixture: every covered pixel must stay transparent.</summary>
public partial class SpeedModeRainProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Use a real renderer for rain readback");
            GetWindow().Size = new Vector2I(1280, 720);
            var viewport = new SubViewport
            {
                Size = new Vector2I(1280, 720), TransparentBg = true,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            var surfaceOrigin = new Vector2I(1500, 1500);
            using var surface = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                bool covered = x >= 320 && x < 960 && y >= 180 && y < 540;
                WriteFixturePixel(surface, x, y, 1500 + MathF.Round(x / 44f * 16) / 16,
                    1500 + MathF.Round(y / 44f * 16) / 16, covered ? 17 : 1, surfaceOrigin);
            }
            using var texture = ImageTexture.CreateFromImage(surface);
            var rain = new SpeedModeRainPass();
            viewport.AddChild(rain);
            int outside = 0;
            for (int frame = 0; frame < 4; frame++)
            {
                rain.Bind(texture, viewport.Size, 1, -0.5f, frame * 0.137, surfaceOrigin: surfaceOrigin);
                await Draw();
                using var picture = viewport.GetTexture().GetImage();
                for (int y = 0; y < 720; y++)
                for (int x = 0; x < 1280; x++)
                {
                    bool visible = picture.GetPixel(x, y).A > 0;
                    if (x >= 320 && x < 960 && y >= 180 && y < 540)
                    {
                        if (visible) throw new InvalidOperationException($"Covered rain pixel at {x},{y}");
                    }
                    else if (visible) outside++;
                }
                string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
                if (frame == 1 && !string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    picture.SavePng(Path.Combine(output, "guo_speed-weather_covered-fixture_still_20261029.png"));
                }
            }
            if (outside < 1000) throw new InvalidOperationException($"Rain failed to render outside cover ({outside} pixels)");

            CheckOwnership(rain, texture, viewport.Size);

            // Isolate the splash component: the right half is an upright card,
            // the left half exposed floor. No splash texel may land on a card.
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
                WriteFixturePixel(surface, x, y, 1500 + MathF.Round(x / 44f * 16) / 16,
                    1500 + MathF.Round(y / 44f * 16) / 16, x < 640 ? 1 : 4, surfaceOrigin);
            texture.Update(surface);
            var material = (ShaderMaterial)rain.Material;
            material.SetShaderParameter("component_mask", 2);
            rain.Bind(texture, viewport.Size, 1, 0, 0.123, surfaceOrigin: surfaceOrigin);
            await Draw();
            using var splashes = viewport.GetTexture().GetImage();
            int floorSplashes = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                if (splashes.GetPixel(x, y).A <= 0) continue;
                if (x >= 640) throw new InvalidOperationException("Splash on an upright card");
                floorSplashes++;
            }
            if (floorSplashes < 100) throw new InvalidOperationException("No floor splashes at real world coordinates");
            // Rebasing the per-frame origin must not move world-anchored rings.
            surfaceOrigin = new Vector2I(1510, 1490);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
                WriteFixturePixel(surface, x, y, 1500 + MathF.Round(x / 44f * 16) / 16,
                    1500 + MathF.Round(y / 44f * 16) / 16, x < 640 ? 1 : 4, surfaceOrigin);
            texture.Update(surface);
            rain.Bind(texture, viewport.Size, 1, 0, 0.123, surfaceOrigin: surfaceOrigin);
            await Draw();
            using var rebased = viewport.GetTexture().GetImage();
            if (!splashes.GetData().AsSpan().SequenceEqual(rebased.GetData()))
                throw new InvalidOperationException("Changing surface origin moved world-space splashes");
            // Explicit time means frame scheduling cannot move the drops.
            rain.Bind(texture, viewport.Size, 1, 0, 0.123, surfaceOrigin: surfaceOrigin);
            await Draw();
            using var repeated = viewport.GetTexture().GetImage();
            if (!splashes.GetData().AsSpan().SequenceEqual(repeated.GetData()))
                throw new InvalidOperationException("Same real time changed the splash frame");

            // Isolate drips below an eight-pixel roof edge, including a
            // covered porch directly beneath part of the edge.
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                int kind = y >= 120 && y < 160 ? 5 : 4;
                if (x >= 320 && x < 960 && y >= 160 && y < 168) kind += 16;
                surface.SetPixel(x, y, new Color(0, 0, 0, kind));
            }
            texture.Update(surface);
            material.SetShaderParameter("component_mask", 4);
            rain.Bind(texture, viewport.Size, 1, 0, 0.173);
            await Draw();
            using var drips = viewport.GetTexture().GetImage();
            int dripPixels = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                if (drips.GetPixel(x, y).A <= 0) continue;
                if (y < 160 || y >= 168 || x >= 320 && x < 960)
                    throw new InvalidOperationException("Drip outside exposed roof edge");
                dripPixels++;
            }
            if (dripPixels < 20) throw new InvalidOperationException("No roof-edge drips");

            rain.Bind(texture, viewport.Size, 0, 0, 3);
            await Draw();
            using var dry = viewport.GetTexture().GetImage();
            foreach (byte alpha in Alpha(dry))
                if (alpha != 0) throw new InvalidOperationException("Rain visible at zero intensity");
            rain.HideForFallback();
            await Draw();
            using var hidden = viewport.GetTexture().GetImage();
            if (hidden.GetPixel(10, 10).A > 0 || rain.Active) throw new InvalidOperationException("Fallback must hide rain");
            rain.Bind(null, viewport.Size, 1, 0, 0);
            if (rain.Active || SpeedModeRainPass.ReplacesClassic(null))
                throw new InvalidOperationException("Invalid targets must not suppress classic rain");
            GD.Print($"[speed-rain] PASS: 0 covered rain pixels in four frames, {outside} exposed pixels, {floorSplashes} floor-only splash pixels at Britain coordinates, {dripPixels} exposed roof-edge pixels; explicit-time determinism, dry and fallback (fixture only)");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("[speed-rain] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    internal static void WriteFixturePixel(Image surface, int x, int y, float worldX, float worldY,
        int classAndFlags, Vector2I origin, float z = 0)
        => surface.SetPixel(x, y, new Color(worldX - origin.X, worldY - origin.Y, z, classAndFlags));

    internal static void CheckOwnership(SpeedModeRainPass rain, Texture2D texture, Vector2I size)
    {
        // This checks identity ownership only. A fully constructed World
        // creates container configuration, which is unrelated to this test.
        // No game state or methods are accessed through these identity keys.
        var world = (GUO.Game.World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GUO.Game.World));
        var otherWorld = (GUO.Game.World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GUO.Game.World));
        uint savedTick = Time.Ticks;
        try
        {
            rain.Bind(texture, size, 1, 0, 0, world, Vector2I.Zero);
            if (!SpeedModeRainPass.ReplacesClassic(world) || SpeedModeRainPass.ReplacesClassic(otherWorld))
                throw new InvalidOperationException("Rain replacement must belong to the bound world");
            Time.Ticks++;
            if (SpeedModeRainPass.ReplacesClassic(world)) throw new InvalidOperationException("Stale frame replaced classic rain");
            rain.Bind(texture, size, 1, 0, 0, world, Vector2I.Zero);
            rain.HideForFallback();
            if (SpeedModeRainPass.ReplacesClassic(world)) throw new InvalidOperationException("Fallback retained classic suppression");
            rain.Bind(texture, size, 1, 0, 0, world, Vector2I.Zero);
            rain.Visible = false;
            if (SpeedModeRainPass.ReplacesClassic(world)) throw new InvalidOperationException("Hidden pass replaced classic rain");
            rain.Bind(texture, size, 1, 0, 0, world);
            if (rain.Active || SpeedModeRainPass.ReplacesClassic(world))
                throw new InvalidOperationException("World-bound rain requires an explicit surface origin");
        }
        finally { Time.Ticks = savedTick; }
    }

    private static System.Collections.Generic.IEnumerable<byte> Alpha(Image picture)
    {
        byte[] bytes = picture.GetData();
        for (int i = 3; i < bytes.Length; i += 4) yield return bytes[i];
    }

    private async System.Threading.Tasks.Task Draw()
    {
        for (int i = 0; i < 2; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }
}
