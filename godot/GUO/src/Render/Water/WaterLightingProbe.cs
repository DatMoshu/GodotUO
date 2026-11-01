// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.Water;

/// <summary>Rendered lighting-water checks with synthetic coast and light-target inputs.</summary>
public partial class WaterLightingProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "use a real renderer for pixel readback");
            GetWindow().Size = new Vector2I(1280, 720);
            var viewport = new SubViewport
            {
                Size = new Vector2I(1280, 720),
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
                mask.SetPixel(x, y, new Color(x + y < 133 ? 1f : 0f, 123f / 255f, 0f, 1f));
            using var maskTexture = ImageTexture.CreateFromImage(mask);
            using var light = Image.CreateEmpty(1280, 720, false, Image.Format.Rgbah);
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                float reach = Math.Max(0f, 1f - new Vector2(x - 640, y - 360).Length() / 220f);
                light.SetPixel(x, y, new Color(0.12f + reach * 2f,
                    0.12f + reach * 0.8f, 0.12f + reach * 0.2f));
            }
            using var lightTexture = ImageTexture.CreateFromImage(light);
            using var shader = new Shader { Code = Code(Godot.FileAccess.GetFileAsString("res://src/Render/shaders/uo_water.gdshaderinc")) };
            using var material = new ShaderMaterial { Shader = shader };
            new LightingWaterStyle().Bind((name, value) => material.SetShaderParameter(name, value));
            material.SetShaderParameter("water_mask", maskTexture);
            material.SetShaderParameter("water_mask_origin", Vector2.Zero);
            material.SetShaderParameter("water_light_target", lightTexture);
            material.SetShaderParameter("water_light_bounds", new Vector4(0f, 0f, 1280f, 720f));
            material.SetShaderParameter("water_light_ambient", new Vector3(0.12f, 0.12f, 0.12f));
            material.SetShaderParameter("water_time", 11f);
            material.SetShaderParameter("water_target_to_world", new Vector4(1f, 1f, -640f, 2500f));
            var rect = new ColorRect { Size = new Vector2(1280, 720), Material = material };
            viewport.AddChild(rect);
            string output = System.Environment.GetEnvironmentVariable("GUO_WATER_PROBE_OUT");
            if (!string.IsNullOrEmpty(output)) Directory.CreateDirectory(output);

            // Optional frozen pre-change shader: all six old model branches
            // must remain byte-identical with environment shading disabled.
            string baseline = System.Environment.GetEnvironmentVariable("GUO_WATER_BASELINE_INCLUDE");
            if (!string.IsNullOrEmpty(baseline))
            {
                using var oldShader = new Shader { Code = Code(File.ReadAllText(baseline)) };
                material.SetShaderParameter("water_environment", false);
                for (int model = 0; model <= 5; model++)
                {
                    material.SetShaderParameter("water_model", model);
                    material.Shader = oldShader;
                    await Draw();
                    using var before = viewport.GetTexture().GetImage();
                    material.Shader = shader;
                    await Draw();
                    using var after = viewport.GetTexture().GetImage();
                    Require(before.GetData().AsSpan().SequenceEqual(after.GetData()), $"model {model} changed with environment off");
                }
                GD.Print("[water-lighting] six old model branches byte-identical to frozen baseline");
            }

            new LightingWaterStyle().Bind((name, value) => material.SetShaderParameter(name, value));
            material.SetShaderParameter("water_daylight", 1f);
            material.SetShaderParameter("water_has_light_target", false);
            await Draw();
            using var day = viewport.GetTexture().GetImage();
            Save(day, "day-on");
            // Identical world pixels through independent quad UVs: splitting
            // a surface into tiles must not restart waves at tile boundaries.
            rect.Visible = false;
            var tiles = new Node2D();
            viewport.AddChild(tiles);
            for (int y = 0; y < 6; y++)
            for (int x = 0; x < 8; x++)
                tiles.AddChild(new ColorRect
                {
                    Position = new Vector2(x * 160, y * 120),
                    Size = new Vector2(160, 120), Material = material
                });
            await Draw();
            using var tiled = viewport.GetTexture().GetImage();
            Save(tiled, "day-independent-quads");
            Require(Difference(day, tiled) == 0, "independent tile UVs must produce identical world-space waves");
            tiles.Visible = false;
            rect.Visible = true;
            material.SetShaderParameter("water_daylight", 0.12f);
            await Draw();
            using var night = viewport.GetTexture().GetImage();
            Save(night, "night-no-lamp");
            Require(Difference(day, night) > 500, "daylight must change sky/glints");
            material.SetShaderParameter("water_light_target", lightTexture);
            material.SetShaderParameter("water_light_bounds", new Vector4(0f, 0f, 1280f, 720f));
            material.SetShaderParameter("water_light_ambient", new Vector3(0.12f, 0.12f, 0.12f));
            material.SetShaderParameter("water_has_light_target", true);
            await Draw();
            using var lit = viewport.GetTexture().GetImage();
            Save(lit, "night-lamp");
            Require(Difference(night, lit) > 100, "nearby warm light must produce surface highlights");
            byte[] unlitBytes = night.GetData(), litBytes = lit.GetData();
            int channels = night.GetFormat() == Image.Format.Rgb8 ? 3 : 4;
            for (int y = 0; y < 720; y++)
            for (int x = 0; x < 1280; x++)
            {
                if ((x - 640) * (x - 640) + (y - 360) * (y - 360) <= 224 * 224) continue;
                int at = (y * 1280 + x) * channels;
                Require(unlitBytes[at] == litBytes[at] && unlitBytes[at + 1] == litBytes[at + 1]
                    && unlitBytes[at + 2] == litBytes[at + 2], "light-target glint escaped its screen-space footprint");
            }
            material.SetShaderParameter("water_has_light_target", false);
            await Draw();
            using var disabled = viewport.GetTexture().GetImage();
            Require(Difference(night, disabled) == 0, "disabling speed target must remove its highlights exactly");
            material.SetShaderParameter("water_wind", -1f);
            await Draw();
            using var wind = viewport.GetTexture().GetImage();
            Save(wind, "night-wind");
            Require(Difference(night, wind) > 500, "wind must move the wave field");
            material.SetShaderParameter("water_wind", 0f);
            var rainField = new WaterRipples();
            rainField.CentreOnForProbe(64, 64);
            var emitter = new RainRippleEmitter();
            for (int i = 0; i < 100; i++)
                emitter.Update(rainField, 0.05f, 1f, true, 8, 64, 64, (x, y) => x + y < 133);
            rainField.StepForProbe(8);
            material.SetShaderParameter("water_ripples", rainField.Texture);
            material.SetShaderParameter("water_ripples_origin", new Vector2(rainField.OriginX, rainField.OriginY));
            await Draw();
            using var rain = viewport.GetTexture().GetImage();
            Save(rain, "night-rain");
            Require(Difference(night, rain) > 500, "live rain emitter must visibly disturb lighting water");
            material.SetShaderParameter("water_model", 0);
            await Draw();
            using var classic = viewport.GetTexture().GetImage();
            Save(classic, "classic");
            GD.Print("[water-lighting] PASS: independent-quad continuity, sky follows daylight, local light highlights, exact target-off restoration, wind-driven waves and visible rain impacts (synthetic coast)");
            GetTree().Quit();

            void Save(Image image, string name)
            {
                if (!string.IsNullOrEmpty(output))
                    image.SavePng(Path.Combine(output, "guo_lighting-water_synthetic-" + name + "_still_20261029.png"));
            }
        }
        catch (Exception e)
        {
            GD.PrintErr("[water-lighting] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    private static string Code(string include) => "shader_type canvas_item;\nrender_mode unshaded;\n" + include + """

        void fragment() {
            vec2 target = SCREEN_UV / SCREEN_PIXEL_SIZE;
            vec2 world = target + vec2(-640.0, 2500.0);
            vec3 native = vec3(0.055, 0.14, 0.19) + vec3(water_hash(floor(world)) * 0.025);
            vec2 f;
            vec2 cell = water_cell(world, f);
            vec3 c = native;
            if (water_model > 0) { c = water_shade(native, target); }
            if (water_mask_at(cell).r < 0.5) { c = vec3(0.27, 0.25, 0.17); }
            COLOR = vec4(c, 1.0);
        }
        """;

    private async System.Threading.Tasks.Task Draw()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private static int Difference(Image a, Image b)
    {
        byte[] x = a.GetData(), y = b.GetData();
        Require(a.GetFormat() == b.GetFormat() && a.GetFormat() is Image.Format.Rgb8 or Image.Format.Rgba8,
            "readback must be RGB8 or RGBA8");
        int channels = a.GetFormat() == Image.Format.Rgb8 ? 3 : 4;
        int different = 0;
        for (int i = 0; i < x.Length; i += channels)
            if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2]) different++;
        return different;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
