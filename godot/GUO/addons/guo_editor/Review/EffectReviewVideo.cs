// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS && GUO_LABS
using System;
using System.IO;
using Environment = System.Environment;
using Godot;
using GUO.Configuration;
using GUO.Renderer.Weather;
using GUO.Labs.WaterRain;
using GUO.Renderer;
using GUO.Renderer.Particles;
using GUO.Renderer.Water;

namespace GUO.Editor.Review;

/// <summary>Offline review footage using production effects on synthetic ground.</summary>
public partial class EffectReviewVideo : Node2D
{
    private UltimaBatcher2D _batcher;
    private readonly AmbientParticles _particles = new();
    public override async void _Ready()
    {
        try
        {
            string folder = Environment.GetEnvironmentVariable("GUO_REVIEW_FRAMES");
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Set GUO_REVIEW_FRAMES");
            string mode = Environment.GetEnvironmentVariable("GUO_REVIEW_MODE") ?? "weather";
            Directory.CreateDirectory(folder);
            GetWindow().Size = new Vector2I(960, 540);
            RenderingServer.SetDefaultClearColor(new Color("20262c"));
            var canvas = new Node2D(); AddChild(canvas);
            _batcher = new UltimaBatcher2D(canvas.GetCanvasItem());
            var title = new Label { Position = new Vector2(28, 20), Text = "UO+ / " + mode.ToUpperInvariant() };
            AddChild(title);
            var status = new Label { Position = new Vector2(28, 58) }; AddChild(status);
            AddChild(new Label { Position = new Vector2(28, 490), Text = "Production effects / synthetic ground / offline review, not live gameplay" });
            var profile = new Profile { AmbientFog = true, AmbientLeaves = true, AmbientDust = true,
                AmbientParticleDensity = 100, AmbientParticleCap = 512 };
            var waves = new WaterRipples(); waves.CentreOnForProbe(1000, 1000); waves.StepForProbe(0);
            var emitter = new RainRippleEmitter();
            using var mask = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            mask.Fill(new Color(1, 123f / 255, 0, 1));
            using var maskTexture = ImageTexture.CreateFromImage(mask);
            // Keep the shader include inside a quoted source line: the C#
            // preprocessor still scans disabled #if blocks in normal builds.
            using var shader = new Shader { Code = "shader_type canvas_item;\nrender_mode unshaded;\n"
                + "#include \"res://src/Render/shaders/uo_water.gdshaderinc\"\n"
                + "void fragment() {\n"
                + "vec2 world = vec2((UV.x - 0.5) * 1100.0, (UV.y - 0.5) * 550.0 + 44020.0);\n"
                + "COLOR = vec4(water_shade(vec3(0.16, 0.28, 0.4), world), 1.0);\n}" };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("water_target_to_world", new Vector4(1, 1, 0, 0));
            material.SetShaderParameter("water_mask", maskTexture);
            material.SetShaderParameter("water_mask_origin", new Vector2(936, 936));
            material.SetShaderParameter("water_ripples_origin", new Vector2(936, 936));
            var water = new ColorRect { Position = new Vector2(28, 110), Size = new Vector2(904, 350), Material = material, Visible = mode == "water" };
            AddChild(water);
            for (int frame = 0; frame < 240; frame++)
            {
                float seconds = frame / 20f;
                _batcher.BeginFrame(); _batcher.Begin();
                if (mode == "water")
                {
                    int model = Math.Min(5, frame / 48 + 1);
                    bool raining = frame % 48 >= 12;
                    emitter.Update(waves, 0.05f, 1, raining, 8, 1000, 1000, (_, _) => true);
                    waves.StepForProbe(1);
                    material.SetShaderParameter("water_model", model);
                    material.SetShaderParameter("water_params", new Vector4(1, 0.045f, 0, 0));
                    material.SetShaderParameter("water_params2", new Vector4(0, 0.25f, model == 2 ? 6 : model == 3 ? 3 : 0, 1));
                    material.SetShaderParameter("water_ripples", waves.Texture);
                    status.Text = new[] { "", "UO COLOURS", "PIXEL", "CEL", "REALISTIC", "CLASSIC+" }[model] + (raining ? " / RAIN IMPACTS" : " / CALM (residual waves may persist)");
                }
                else if (mode == "particles")
                {
                    bool on = frame >= 30 && frame < 210;
                    profile.AmbientFog = profile.AmbientLeaves = profile.AmbientDust = on;
                    _particles.Begin(profile, seconds);
                    status.Text = (on ? "ENABLED" : "DISABLED") + " / fog banks | drifting leaves | dry-area dust";
                    for (int panel = 0; panel < 3; panel++)
                    {
                        var color = panel == 0 ? new GUO.Compat.Color(43, 62, 57) : panel == 1 ? new GUO.Compat.Color(48, 62, 35) : new GUO.Compat.Color(113, 94, 61);
                        _batcher.Draw(SolidColorTextureCache.GetTexture(color), new GUO.Compat.Rectangle(28 + panel * 302, 110, 296, 350), Vector3.Back, 0);
                        for (int y = 0; y < 24; y++) for (int x = 0; x < 18; x++)
                            if (_particles.TrySample(x, y, panel, (AmbientParticleKind)(panel + 1), out var p))
                                _particles.Draw(_batcher, p, 40 + panel * 302 + x * 15, 140 + y * 12, 1);
                    }
                }
                else
                {
                    status.Text = "DRY | PUDDLE FILLING | PUDDLE + RAIN | WATER + RAIN";
                    for (int panel = 0; panel < 4; panel++)
                    for (int tx = 0; tx < 4; tx++) for (int ty = 0; ty < 4; ty++)
                    {
                        int x = 70 + panel * 230 + (tx - ty) * 22, y = 160 + (tx + ty) * 22;
                        var color = panel == 3 ? new GUO.Compat.Color(40, 73, 100) : new GUO.Compat.Color(92, 89, 82);
                        for (int dy = -21; dy <= 21; dy++)
                        {
                            int half = 22 - Math.Abs(dy);
                            _batcher.Draw(SolidColorTextureCache.GetTexture(color), new GUO.Compat.Rectangle(x + 22 - half, y + 22 + dy, half * 2, 1), null, WeatherLab.Hue(1), 0, Vector2.Zero, SpriteEffects.None, 0);
                        }
                        var surface = new WeatherSurface(null) { Water = panel == 3,
                            Fill = panel == 1 ? Math.Min(1, seconds / 8) : panel == 2 ? 1 : 0,
                            RingAge = panel >= 2 ? (seconds * 1.5f + (tx + ty) * 0.23f) % 1 : 1 };
                        surface.Draw(_batcher, x, y, 0);
                    }
                }
                _batcher.End();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(folder, $"{frame:D4}.png")) != Error.Ok) throw new IOException("Frame capture failed");
            }
            GD.Print("[effect review video] PASS: 240 actual rendered frames / " + mode);
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr(e); GetTree().Quit(1); }
    }
    public override void _ExitTree() { _batcher?.Dispose(); _particles.Dispose(); }
}
#endif
