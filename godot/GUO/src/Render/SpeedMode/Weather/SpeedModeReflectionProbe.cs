// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;
using GUO.Renderer.Water;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Approved four-tap geometric reflection fixture, independent of object base Z.</summary>
public partial class SpeedModeReflectionProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Use a real renderer for reflection readback");
            GetWindow().Size = new Vector2I(1280, 720);
            var viewport = new SubViewport
            {
                Size = new Vector2I(1280, 720),
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            using var colors = Image.CreateEmpty(1280, 720, false, Image.Format.Rgba8);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                Color color = new(0.3f, 0.25f, 0.2f);
                if (y >= 240 && y < 320 && x >= 64 && x < 960)
                    color = x < 320 ? new Color(1, 0.1f, 0.1f) : x < 576
                        ? new Color(0.1f, 1, 0.1f) : new Color(0.1f, 0.1f, 1);
                colors.SetPixel(x, y, color);
            }
            using var colorTexture = ImageTexture.CreateFromImage(colors);
            var background = new TextureRect { Texture = colorTexture, Size = viewport.Size };
            viewport.AddChild(background);
            using var light = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            light.Fill(new Color(0, 0, 0));
            using var lightTexture = ImageTexture.CreateFromImage(light);
            using var surface = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            using var surfaceTexture = ImageTexture.CreateFromImage(surface);
            var wet = new SpeedModeWetPass();
            viewport.AddChild(wet);
            int total = 0;
            foreach (int scale in new[] { 1, 2 })
            {
                var origin = new Vector2I(6000, 4000);
                Fill(surface, origin, scale, false);
                surfaceTexture.Update(surface);
                wet.Bind(surfaceTexture, lightTexture, viewport.Size, origin, 1, 0, 0, scale);
                await Draw();
                using var before = viewport.GetTexture().GetImage();
                wet.Bind(surfaceTexture, lightTexture, viewport.Size, origin, 1, 0, 0, scale, true);
                await Draw();
                using var after = viewport.GetTexture().GetImage();
                total += Check(before, after, scale);
                origin += new Vector2I(16, -16);
                Fill(surface, origin, scale, false);
                surfaceTexture.Update(surface);
                wet.Bind(surfaceTexture, lightTexture, viewport.Size, origin, 1, 0, 0, scale, true);
                await Draw();
                using var rebased = viewport.GetTexture().GetImage();
                Require(Same(after, rebased), "Origin rebasing must not change reflected candidates");
                Fill(surface, origin, scale, false, 40);
                surfaceTexture.Update(surface);
                wet.Bind(surfaceTexture, lightTexture, viewport.Size, origin, 1, 0, 0, scale, true);
                await Draw();
                using var upperStorey = viewport.GetTexture().GetImage();
                Require(Same(after, upperStorey), "The same relative geometry on another storey must reflect identically");
                wet.Bind(surfaceTexture, lightTexture, viewport.Size, origin, 1, 0, 0, scale);
                await Draw();
                using var off = viewport.GetTexture().GetImage();
                Require(Same(before, off), "Fixture reflection off must restore wet shading exactly");
                if (scale == 1) Save(after, "wet-reflections");
            }
            wet.HideForFallback();
            using var waterShader = new Shader
            {
                Code = "shader_type canvas_item; render_mode unshaded;\n"
                    + Godot.FileAccess.GetFileAsString("res://src/Render/shaders/uo_water.gdshaderinc")
                    + "\nvoid fragment() { COLOR = vec4(water_shade(vec3(0.12,0.18,0.22), UV * water_reflection_target_size),1.0); }"
            };
            using var water = new ShaderMaterial { Shader = waterShader };
            new LightingWaterStyle().Bind((name, value) => water.SetShaderParameter(name, value));
            var waterRect = new ColorRect { Size = viewport.Size, Material = water };
            viewport.AddChild(waterRect);
            var waterOrigin = new Vector2I(6000, 4000);
            Fill(surface, waterOrigin, 1, true);
            surfaceTexture.Update(surface);
            water.SetShaderParameter("water_surface_buffer", surfaceTexture);
            water.SetShaderParameter("water_scene_color", colorTexture);
            water.SetShaderParameter("water_reflection_target_size", new Vector2(1280, 720));
            water.SetShaderParameter("water_reflection_origin", new Vector2(6000, 4000));
            await Draw();
            using var waterBefore = viewport.GetTexture().GetImage();
            water.SetShaderParameter("water_has_reflections", true);
            await Draw();
            using var waterAfter = viewport.GetTexture().GetImage();
            total += Check(waterBefore, waterAfter, 1);
            Save(waterAfter, "water-reflections");
            water.SetShaderParameter("water_has_reflections", false);
            await Draw();
            using var waterOff = viewport.GetTexture().GetImage();
            Require(Same(waterBefore, waterOff), "Fixture reflection off must restore lighting water exactly");
            GD.Print($"[speed-reflection] PASS: {total} pixels; wet/water, upright/both wall classes, 1x/2x grid, four-tap height/column consistency, wrong height/class rejection, cover/off, origin and storey-offset parity; no base-Z dependency.");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("[speed-reflection] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    private static void Fill(Image image, Vector2I origin, int scale, bool water, float planeZ = 0)
    {
        for (int y = 0; y < 720; y++)
        for (int x = 0; x < 1280; x++)
        {
            bool card = y >= 240 && y < 320 && x >= 64 && x < 832;
            int kind = card ? x < 320 ? 4 : x < 576 ? 2 : 3 : water ? 6 : 33;
            if (!card && x >= 200 && x < 224) kind += 16;
            float sx = x / (float)scale, sy = (card ? 320 : y) / (float)scale;
            float z = card ? (320 - y) / (4f * scale) : 0;
            if (card && x >= 704) z += 20; // Inconsistent reflected elevation: reject.
            z += planeZ;
            float worldX = 6000 + MathF.Round((sx + sy) / 44 * 16) / 16;
            float worldY = 4000 + MathF.Round((sy - sx) / 44 * 16) / 16;
            SpeedModeRainProbe.WriteFixturePixel(image, x, y, worldX, worldY, kind, origin, z);
        }
    }

    private static int Check(Image before, Image after, int scale)
    {
        int[] counts = new int[3];
        for (int y = 0; y < 720; y++)
        for (int x = 0; x < 1280; x++)
        {
            if (before.GetPixel(x, y) == after.GetPixel(x, y)) continue;
            Require(y >= 320 && y <= 320 + 32 * scale + 3 * scale,
                "Reflection escaped the four-tap vertical footprint");
            Require(x >= 60 && x < 708 && !(x >= 200 && x < 224),
                "Reflection leaked to wrong class/elevation, empty column or covered receiver");
            counts[x < 320 ? 0 : x < 576 ? 1 : 2]++;
        }
        foreach (int count in counts) Require(count > 20, "Upright and both wall directions need reflected candidates");
        return counts[0] + counts[1] + counts[2];
    }

    private static void Save(Image image, string name)
    {
        string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        image.SavePng(Path.Combine(output, "guo_speed-weather_" + name + "_20261029.png"));
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
