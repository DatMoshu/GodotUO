// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.Water;

/// <summary>Real shader visibility of a spreading ripple over dark water; synthetic inputs only.</summary>
public partial class WaterRippleProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            var viewport = new SubViewport
            {
                Size = new Vector2I(512, 256),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            mask.Fill(new Color(1f, 123f / 255f, 0f, 1f));
            using var maskTexture = ImageTexture.CreateFromImage(mask);
            using var flat = Image.CreateEmpty(WaterRipples.Size, WaterRipples.Size, false, Image.Format.R8);
            flat.Fill(new Color(128f / 255f, 0f, 0f));
            using var flatTexture = ImageTexture.CreateFromImage(flat);
            var ripples = new WaterRipples { Damping = 0.98f };
            ripples.CentreOnForProbe(0, 0);
            ripples.Stamp(0, 0, 0.6f, 0.8f);
            ripples.StepForProbe(30);
            using var shader = new Shader
            {
                Code = """
                    shader_type canvas_item;
                    render_mode unshaded;
                    #include "res://src/Render/shaders/uo_water.gdshaderinc"
                    void fragment() {
                        COLOR = vec4(water_shade(vec3(0.07, 0.13, 0.21), FRAGCOORD.xy), 1.0);
                    }
                    """
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("water_model", 2);
            material.SetShaderParameter("water_mask", maskTexture);
            material.SetShaderParameter("water_mask_origin", new Vector2(-64f, -64f));
            material.SetShaderParameter("water_target_to_world", new Vector4(1f, 1f, -256f, -148f));
            material.SetShaderParameter("water_ripples_origin", new Vector2(-64f, -64f));
            // Isolate the ripple from ambient swell, shore foam and glints.
            material.SetShaderParameter("water_params", new Vector4(0.8f, 0.035f, 0f, 0f));
            material.SetShaderParameter("water_params2", new Vector4(0f, 0.25f, 3f, 1f));
            material.SetShaderParameter("water_params3", new Vector4(2f, 0f, 0f, 0f));
            var rect = new ColorRect { Size = new Vector2(512, 256), Material = material };
            viewport.AddChild(rect);
            material.SetShaderParameter("water_ripples", flatTexture);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var calm = viewport.GetTexture().GetImage();
            material.SetShaderParameter("water_ripples", ripples.Texture);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var wave = viewport.GetTexture().GetImage();
            int visible = 0;
            float max = 0f, brightest = 0f;
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    Color a = calm.GetPixel(x, y), b = wave.GetPixel(x, y);
                    float difference = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                    max = Math.Max(max, difference);
                    brightest = Math.Max(brightest, Math.Max(b.R, Math.Max(b.G, b.B)));
                    if (difference >= 0.04f) visible++;
                }
            }
            string output = System.Environment.GetEnvironmentVariable("GUO_WATER_PROBE_OUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                calm.SavePng(Path.Combine(output, "guo_WATER-03_pixel-calm_still_20261019-0452.png"));
                wave.SavePng(Path.Combine(output, "guo_WATER-03_pixel-ripple_still_20261019-0452.png"));
            }
            GD.Print($"[water ripple] visible pixels={visible}, maximum channel contrast={max:F4}, brightest={brightest:F4}");
            if (brightest > 0.6f)
                throw new InvalidOperationException("zero glint must not draw white sparkles on dark water");
            if (visible < 500 || max < 0.16f)
                throw new InvalidOperationException("a spreading ripple must retain two palette steps of contrast on dark water");
            GD.Print("[water ripple] PASS: visible spreading ring over dark pixel water");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water ripple] FAIL: " + ex.Message);
            GetTree().Quit(1);
        }
    }
}
