// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer.Water;

/// <summary>
/// Rendered regression for diagonal surf bars (WATER-05). Run this scene
/// with a real renderer, once with forward_plus and once with gl_compatibility.
/// All inputs are synthetic; no shard or client artwork is needed.
/// </summary>
public partial class WaterShoreProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            var viewport = new SubViewport
            {
                Size = new Vector2I(8, 1),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = false
            };
            AddChild(viewport);
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    mask.SetPixel(x, y, new Color(x + y >= 128 ? 0f : 1f, 123f / 255f, 0f, 1f));
                }
            }
            using var texture = ImageTexture.CreateFromImage(mask);
            using var shader = new Shader
            {
                Code = """
                    shader_type canvas_item;
                    render_mode unshaded;
                    #include "res://src/Render/shaders/uo_water.gdshaderinc"
                    void fragment() {
                        // The old edge ramps draw a bar at the second point,
                        // even though neither point is on the coastline.
                        vec2 xy = vec2(63.499, 63.45);
                        int at = int(FRAGCOORD.x);
                        if (at == 1) { xy.x = 63.501; }
                        if (at == 2) { xy = vec2(63.707107); }
                        if (at == 3) { xy = vec2(63.6); }
                        if (at == 4) { xy = vec2(60.0); }
                        if (at == 5) { xy = vec2(66.0); }
                        vec2 cell = floor(xy + 0.5);
                        float shore = water_shore(cell, xy - cell);
                        COLOR = vec4(vec3(shore), 1.0);
                    }
                    """
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("water_mask", texture);
            material.SetShaderParameter("water_mask_origin", Vector2.Zero);
            material.SetShaderParameter("water_params2", new Vector4(0f, 0.25f, 0f, 0f));
            var rect = new ColorRect { Size = new Vector2(8, 1), Material = material };
            viewport.AddChild(rect);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var rendered = viewport.GetTexture().GetImage())
            {
                Require(rendered.GetPixel(0, 0).R < 0.02f && rendered.GetPixel(1, 0).R < 0.02f,
                    "an internal diagonal tile boundary must not draw a surf bar");
                Require(rendered.GetPixel(2, 0).R > 0.95f, "the actual diagonal coastline must retain surf");
                Require(rendered.GetPixel(3, 0).R > 0.1f && rendered.GetPixel(3, 0).R < 0.8f,
                    "surf must fall off across the reconstructed coastline");
                Require(rendered.GetPixel(4, 0).R < 0.02f && rendered.GetPixel(5, 0).R < 0.02f,
                    "open water and a dry-cell sprite overlap must not fill with foam");
            }
            // Raise only the dry ground: the same contour is now a cliff.
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    if (x + y >= 128)
                    {
                        mask.SetPixel(x, y, new Color(0f, 148f / 255f, 0f, 1f));
                    }
                }
            }
            texture.Update(mask);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var rendered = viewport.GetTexture().GetImage())
            {
                Require(rendered.GetPixel(2, 0).R < 0.02f, "high cliffs must not produce surf");
            }
            // Exercise the real hue core with Classic's unset water bit:
            // binding UO+ uniforms must not change any native output bytes.
            shader.Code = """
                shader_type canvas_item;
                render_mode unshaded;
                #include "res://src/Render/shaders/uo_hue_core.gdshaderinc"
                void vertex() { packed_hue = vec4(0.0, 0.0, 0.0, 1.0); land_normal = vec3(0.0, 0.0, 1.0); }
                void fragment() {
                    COLOR = uo_shade(vec4(FRAGCOORD.x / 8.0, 0.2, 0.4, 1.0), SCREEN_UV, SCREEN_PIXEL_SIZE);
                }
                """;
            material.SetShaderParameter("water_model", 0);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var classicImage = viewport.GetTexture().GetImage();
            byte[] classic = classicImage.GetData();
            Require(classic.Length > 0 && classicImage.GetPixel(0, 0).R != classicImage.GetPixel(7, 0).R
                && classicImage.GetPixel(0, 0).A == 1f,
                "the Classic comparison must contain a rendered opaque colour ramp");
            material.SetShaderParameter("water_model", 5);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var styledImage = viewport.GetTexture().GetImage();
            byte[] styledUniforms = styledImage.GetData();
            Require(classic.AsSpan().SequenceEqual(styledUniforms), "Classic's unset bit must preserve every rendered byte");
            GD.Print("[water shore] PASS: no internal-edge bars, diagonal contour and falloff, no dry overlap fill, cliffs excluded, Classic byte parity");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water shore] FAIL: " + ex.Message);
            GetTree().Quit(1);
        }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
