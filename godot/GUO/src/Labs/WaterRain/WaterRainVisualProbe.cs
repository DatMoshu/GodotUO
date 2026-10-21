// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using Godot;
using GUO.Renderer.Water;

namespace GUO.Labs.WaterRain;

/// <summary>Real water shader + real wave field, with synthetic colours and water mask.</summary>
public partial class WaterRainVisualProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "run with a real renderer without --headless");
            var calm = new WaterRipples();
            var rain = new WaterRipples();
            calm.CentreOnForProbe(1000, 1000);
            rain.CentreOnForProbe(1000, 1000);
            calm.StepForProbe(0);
            var emitter = new RainRippleEmitter();
            for (int i = 0; i < 100; i++)
            {
                emitter.Update(rain, 0.05f, 1, true, 8, 1000, 1000, (_, _) => true);
                rain.StepForProbe(1);
            }
            Require(rain.Stamps == 200, "five seconds of full-intensity rain must stamp 200 impacts");
            var viewport = new SubViewport
            {
                Size = new Vector2I(256, 128), TransparentBg = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            mask.Fill(new Color(1, 123f / 255, 0, 1));
            using var maskTexture = ImageTexture.CreateFromImage(mask);
            using var shader = new Shader { Code = """
                shader_type canvas_item;
                render_mode unshaded;
                #include "res://src/Render/shaders/uo_water.gdshaderinc"
                void fragment() {
                    vec2 world = vec2((UV.x - 0.5) * 1760.0, (UV.y - 0.5) * 880.0 + 44020.0);
                    COLOR = vec4(water_shade(vec3(0.16, 0.28, 0.4), world), 1.0);
                }
                """ };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("water_target_to_world", new Vector4(1, 1, 0, 0));
            material.SetShaderParameter("water_mask", maskTexture);
            material.SetShaderParameter("water_mask_origin", new Vector2(936, 936));
            material.SetShaderParameter("water_ripples_origin", new Vector2(936, 936));
            material.SetShaderParameter("water_params", new Vector4(0, 0.045f, 0, 0));
            viewport.AddChild(new ColorRect { Size = new Vector2(256, 128), Material = material });
            using var proof = Image.CreateEmpty(512, 640, false, Image.Format.Rgba8);
            for (int model = 1; model <= 5; model++)
            {
                material.SetShaderParameter("water_model", model);
                material.SetShaderParameter("water_params2", new Vector4(0, 0.25f, model == 2 ? 6 : model == 3 ? 3 : 0, 1));
                material.SetShaderParameter("water_ripples", calm.Texture);
                await DrawFrame();
                using var before = viewport.GetTexture().GetImage();
                Require(before.GetPixel(0, 0).A > 0.99f && before.GetPixel(0, 0).B > 0.05f,
                    $"model {model}: baseline must contain rendered opaque water");
                // Disabled rain cannot alter the real field's texture bytes.
                using var flatBefore = calm.Texture.GetImage();
                byte[] flat = flatBefore.GetData();
                emitter.Update(calm, 0.25f, 1, false, 8, 1000, 1000, (_, _) => true);
                calm.StepForProbe(0);
                using var flatImage = calm.Texture.GetImage();
                Require(flat.AsSpan().SequenceEqual(flatImage.GetData()), "off-state field bytes must match");
                material.SetShaderParameter("water_ripples", rain.Texture);
                await DrawFrame();
                using var after = viewport.GetTexture().GetImage();
                int changed = 0;
                for (int y = 0; y < 128; y++)
                for (int x = 0; x < 256; x++)
                    if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;
                Require(changed > 0, $"model {model}: rain must change rendered water pixels");
                GD.Print($"[water rain visual] model {model}: {changed} changed pixels");
                before.Convert(Image.Format.Rgba8);
                after.Convert(Image.Format.Rgba8);
                proof.BlitRect(before, new Rect2I(0, 0, 256, 128), new Vector2I(0, (model - 1) * 128));
                proof.BlitRect(after, new Rect2I(0, 0, 256, 128), new Vector2I(256, (model - 1) * 128));
                Require(proof.GetPixel(0, (model - 1) * 128).A > 0.99f
                    && proof.GetPixel(256, (model - 1) * 128).B > 0.05f, "proof must contain both rendered images");
            }
            string output = System.Environment.GetEnvironmentVariable("GUO_WATER_RAIN_PROOF");
            if (!string.IsNullOrWhiteSpace(output))
                Require(proof.SavePng(output) == Error.Ok, "could not save GUO_WATER_RAIN_PROOF");
            GD.Print("[water rain visual] PASS: all five production water models respond to rain, disabled field byte parity (left calm / right rain)");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[water rain visual] FAIL: " + ex); GetTree().Quit(1); }
    }

    private async System.Threading.Tasks.Task DrawFrame()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
#endif
