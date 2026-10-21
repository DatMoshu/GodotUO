// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer.Water;

/// <summary>
/// Rendered regression for diagonal surf bars (WATER-06). Run this scene
/// with a real renderer, once with forward_plus and once with gl_compatibility.
/// All inputs are synthetic; no shard or client artwork is needed.
/// </summary>
public partial class WaterShoreProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless",
                "use a real renderer without --headless; the dummy renderer cannot read back shore pixels");
            var viewport = new SubViewport
            {
                Size = new Vector2I(8, 1),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = false
            };
            AddChild(viewport);
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            using var texture = ImageTexture.CreateFromImage(mask);
            using var shader = new Shader
            {
                Code = """
                    shader_type canvas_item;
                    render_mode unshaded;
                    #include "res://src/Render/shaders/uo_water.gdshaderinc"
                    uniform vec4 probe_basis;
                    uniform vec2 probe_center;
                    uniform bool probe_axis;
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
                        xy -= vec2(64.0);
                        if (probe_axis) {
                            xy = vec2(-1.0, 0.0);
                            if (at == 1) { xy.x = -0.875; }
                            if (at == 2) { xy.x = -0.5; }
                            if (at == 3) { xy.x = -0.625; }
                            if (at == 4) { xy.x = -4.0; }
                            if (at == 5) { xy.x = 2.0; }
                        }
                        xy = probe_center + probe_basis.xy * xy.x + probe_basis.zw * xy.y;
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
            int cases = 0;
            // Rotation tests all four dry sides. Nonzero and negative windows
            // exercise the ring lookup across both texture seams, not just UV 0..1.
            Vector2I[] origins = { new(0, 0), new(-64, 193), new(192, -64) };
            Vector2I[] axes = { new(1, 0), new(0, 1), new(-1, 0), new(0, -1) };
            foreach (bool axis in new[] { false, true })
            foreach (Vector2I origin in origins)
            foreach (Vector2I bx in axes)
            foreach (int height in new[] { 8, 9 })
            {
                Vector2I by = new(-bx.Y, bx.X);
                Vector2I center = origin + new Vector2I(64, 64);
                int waterZ = origin.X == 0 ? -5 : origin.X < 0 ? -15 : 5;
                for (int y = origin.Y; y < origin.Y + 128; y++)
                for (int x = origin.X; x < origin.X + 128; x++)
                {
                    Vector2I delta = new Vector2I(x, y) - center;
                    int localX = delta.X * bx.X + delta.Y * bx.Y;
                    int localY = delta.X * by.X + delta.Y * by.Y;
                    bool dry = (axis ? localX : localX + localY) >= 0;
                    mask.SetPixel(x & 127, y & 127,
                        new Color(dry ? 0f : 1f, (waterZ + (dry ? height : 0) + 128f) / 255f, 0f, 1f));
                }
                texture.Update(mask);
                material.SetShaderParameter("water_mask_origin", (Vector2)origin);
                material.SetShaderParameter("probe_center", (Vector2)center);
                material.SetShaderParameter("probe_basis", new Vector4(bx.X, bx.Y, by.X, by.Y));
                material.SetShaderParameter("probe_axis", axis);
                await DrawFrame();
                using var rendered = viewport.GetTexture().GetImage();
                string context = $"{(axis ? "axis" : "diagonal")} origin={origin} direction={bx} waterZ={waterZ} rise={height}";
                if (height == 9)
                {
                    for (int x = 0; x < 6; x++)
                    {
                        Require(rendered.GetPixel(x, 0).R < 0.02f, context + ": high cliffs must not produce surf");
                    }
                }
                else
                {
                    Require(rendered.GetPixel(0, 0).R < 0.02f && rendered.GetPixel(1, 0).R < 0.02f,
                        context + ": an internal tile boundary must not draw a surf bar");
                    Require(rendered.GetPixel(2, 0).R > 0.95f, context + ": a beach exactly 8 above water must retain surf");
                    Require(rendered.GetPixel(3, 0).R > 0.1f && rendered.GetPixel(3, 0).R < 0.8f,
                        context + ": surf must fall off across the reconstructed coastline");
                    Require(rendered.GetPixel(4, 0).R < 0.02f && rendered.GetPixel(5, 0).R < 0.02f,
                        context + ": open water and a dry-cell sprite overlap must not fill with foam");
                }
                cases++;
            }
            // Exercise the real hue core with Classic's unset water bit:
            // binding UO+ uniforms must not change any native output bytes.
            shader.Code = """
                shader_type canvas_item;
                render_mode unshaded;
                #include "res://src/Render/shaders/uo_hue_core.gdshaderinc"
                uniform float probe_mode = 0.0;
                void vertex() { packed_hue = vec4(0.0, 0.0, probe_mode / 255.0, 1.0); land_normal = vec3(0.0, 0.0, 1.0); }
                void fragment() {
                    COLOR = uo_shade(vec4(FRAGCOORD.x / 8.0, 0.2, 0.4, 1.0), SCREEN_UV, SCREEN_PIXEL_SIZE);
                }
                """;
            material.SetShaderParameter("water_model", 0);
            await DrawFrame();
            using var classicImage = viewport.GetTexture().GetImage();
            byte[] classic = classicImage.GetData();
            Require(classic.Length > 0 && classicImage.GetPixel(0, 0).R != classicImage.GetPixel(7, 0).R
                && classicImage.GetPixel(0, 0).A == 1f,
                "the Classic comparison must contain a rendered opaque colour ramp");
            // A debug colour proves the same material can reach the water
            // branch, so an inert or unbound shader cannot pass the parity check.
            material.SetShaderParameter("water_debug", 1);
            for (int model = 1; model <= 5; model++)
            {
                material.SetShaderParameter("water_model", model);
                await DrawFrame();
                using var styledImage = viewport.GetTexture().GetImage();
                Require(classic.AsSpan().SequenceEqual(styledImage.GetData()),
                    $"model {model}: Classic's unset bit must preserve every rendered byte");
            }
            material.SetShaderParameter("probe_mode", 64f);
            await DrawFrame();
            using var waterImage = viewport.GetTexture().GetImage();
            Color marked = waterImage.GetPixel(0, 0);
            Require(marked.R > 0.95f && marked.G < 0.02f && marked.B > 0.95f,
                "setting the water bit must reach the debug branch in the real hue core");
            GD.Print($"[water shore] PASS: {cases} coast cases (axis/diagonal, four directions, wrapped/negative windows, beach +8/cliff +9), five-model Classic byte parity, water-bit positive control");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water shore] FAIL: " + ex.Message);
            GetTree().Quit(1);
        }
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
