// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Standalone wet-floor fixture; does not claim bank/live-world acceptance.</summary>
public partial class SpeedModeWetProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Use a real renderer for wet-floor readback");
            GetWindow().Size = new Vector2I(1280, 720);
            var viewport = new SubViewport
            {
                Size = new Vector2I(1280, 720),
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            viewport.AddChild(new ColorRect { Size = viewport.Size, Color = new Color(0.3f, 0.25f, 0.2f) });
            var surfaceOrigin = new Vector2I(1500, 1500);
            using var surface = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                int kind = x >= 1120 ? 4 : x < 640 ? 33 : 1;
                if (x >= 320 && x < 960 && y >= 180 && y < 540) kind += 16;
                SpeedModeRainProbe.WriteFixturePixel(surface, x, y, 1500 + MathF.Round(x / 44f * 16) / 16,
                    1500 + MathF.Round(y / 44f * 16) / 16, kind, surfaceOrigin);
            }
            using var surfaceTexture = ImageTexture.CreateFromImage(surface);
            using var light = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            light.Fill(new Color(0, 0, 0));
            using var lightTexture = ImageTexture.CreateFromImage(light);
            var wet = new SpeedModeWetPass();
            viewport.AddChild(wet);
            wet.Bind(surfaceTexture, lightTexture, viewport.Size, surfaceOrigin, 0, 0, 0);
            await Draw();
            using var dry = viewport.GetTexture().GetImage();
            wet.Bind(surfaceTexture, lightTexture, viewport.Size, surfaceOrigin, 1, 0, 0.123);
            await Draw();
            using var dark = viewport.GetTexture().GetImage();
            int wetPixels = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                var before = dry.GetPixel(x, y);
                var after = dark.GetPixel(x, y);
                bool protectedPixel = x >= 1120 || x >= 320 && x < 960 && y >= 180 && y < 540;
                if (protectedPixel) Require(before == after, $"Wet shading leaked to protected pixel {x},{y}");
                else { Require(after.R < before.R, "Exposed floor must darken"); wetPixels++; }
            }
            wet.Bind(surfaceTexture, lightTexture, viewport.Size, surfaceOrigin, 1, 1, 0.123);
            await Draw();
            using var rings = viewport.GetTexture().GetImage();
            int ringPixels = 0;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                if (dark.GetPixel(x, y) == rings.GetPixel(x, y)) continue;
                Require(x < 640 && !(x >= 320 && y >= 180 && y < 540), "Puddle ring outside exposed allowlisted floor");
                ringPixels++;
            }
            Require(ringPixels > 100, "Puddle rings must be visible");
            light.Fill(new Color(1.2f, 0.5f, 0.15f));
            lightTexture.Update(light);
            await Draw();
            using var glints = viewport.GetTexture().GetImage();
            Require(glints.GetPixel(100, 100).R > rings.GetPixel(100, 100).R, "Local light must affect wet ground");
            wet.HideForFallback();
            await Draw();
            using var off = viewport.GetTexture().GetImage();
            Require(dry.GetData().AsSpan().SequenceEqual(off.GetData()), "Off must restore the dry frame exactly");
            Require(SpeedModePuddleSurfaces.IsCandidate(true, 0x3E9, ""), "land cobble id");
            Require(!SpeedModePuddleSurfaces.IsCandidate(false, 0x3E9, ""), "separate static id space");
            Require(SpeedModePuddleSurfaces.IsCandidate(false, 0x515, ""), "static cobble id");
            Require(SpeedModePuddleSurfaces.IsCandidate(false, 0, "Stone Pavers"), "named paving");
            Require(!SpeedModePuddleSurfaces.IsCandidate(true, 0, "grass"), "grass excluded");
            string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                dry.SavePng(Path.Combine(output, "guo_speed-weather_wet-floor_before_20261029.png"));
                glints.SavePng(Path.Combine(output, "guo_speed-weather_wet-floor_after_20261029.png"));
            }
            GD.Print($"[speed-wet] PASS: {wetPixels} darkened floor pixels, covered/card parity, {ringPixels} allowlisted puddle ring pixels, local glints and exact off restoration (fixture only)");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("[speed-wet] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    private async System.Threading.Tasks.Task Draw()
    {
        for (int i = 0; i < 2; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
