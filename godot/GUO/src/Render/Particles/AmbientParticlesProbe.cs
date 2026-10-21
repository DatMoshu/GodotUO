// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer.Particles;

/// <summary>Offline UO+ particle contract checks, with no shard or client data.</summary>
public partial class AmbientParticlesProbe : Node
{
    public override void _Ready()
    {
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--render-particles") >= 0) RenderProof();
        else GetTree().Quit(Run() ? 0 : 1);
    }

    private async void RenderProof()
    {
        try
        {
            Check(Run(), "contracts before rendering");
            string folder = OS.GetEnvironment("GUO_PARTICLES_PROOF");
            Check(!string.IsNullOrWhiteSpace(folder), "set GUO_PARTICLES_PROOF to an evidence folder");
            using var target = new RenderTarget2D(this, 960, 480);
            using var batcher = new UltimaBatcher2D(target.CanvasItem);
            using var particles = new AmbientParticles();
            var p = new Profile { AmbientFog = true, AmbientLeaves = true, AmbientDust = true,
                AmbientParticleDensity = 100, AmbientParticleCap = 512 };
            Image baseline = null;
            foreach (string frame in new[] { "baseline", "enabled-a", "enabled-b", "disabled" })
            {
                bool enabled = frame.StartsWith("enabled");
                p.AmbientFog = p.AmbientLeaves = p.AmbientDust = enabled;
                particles.Begin(p, frame == "enabled-b" ? 5 : 2);
                batcher.BeginFrame();
                batcher.Begin();
                for (int panel = 0; panel < 3; panel++)
                {
                    // Synthetic ground, deliberately free of proprietary UO art.
                    var ground = panel == 0 ? new GUO.Compat.Color(43, 62, 57)
                        : panel == 1 ? new GUO.Compat.Color(48, 62, 35) : new GUO.Compat.Color(113, 94, 61);
                    batcher.Draw(SolidColorTextureCache.GetTexture(ground), new GUO.Compat.Rectangle(panel * 320, 0, 320, 480), Vector3.Back, 0);
                    for (int y = 0; y < 30; y++)
                    for (int x = 0; x < 18; x++)
                    {
                        var kind = (AmbientParticleKind)(panel + 1);
                        if (particles.TrySample(x, y, panel, kind, out var particle))
                            particles.Draw(batcher, particle, panel * 320 + 24 + x * 15, 40 + y * 14, 1);
                    }
                }
                batcher.End();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using Image image = target.Texture.GetImage();
                Check(image.SavePng(System.IO.Path.Combine(folder, "particles-" + frame + ".png")) == Error.Ok, "save evidence");
                if (frame == "baseline") baseline = Image.CreateFromData(960, 480, false, Image.Format.Rgba8, image.GetData());
                else
                {
                    byte[] expected = baseline.GetData(), actual = image.GetData();
                    int differing = 0;
                    for (int i = 0; i < expected.Length; i += 4)
                        if (expected[i] != actual[i] || expected[i + 1] != actual[i + 1] || expected[i + 2] != actual[i + 2]) differing++;
                    Check(enabled ? differing > 500 : differing == 0, "enabled visible / disabled zero pixels");
                    GD.Print($"[Ambient particles render] {frame}: pixels={differing} emitted={particles.Emitted} samples={particles.Samples}");
                }
            }
            baseline?.Dispose();
            SolidColorTextureCache.Clear();
            GD.Print("[Ambient particles render] PASS: synthetic fog | leaves | dust; disabled zero pixel difference");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("[Ambient particles render] FAIL: " + e); GetTree().Quit(1); }
    }

    internal static bool Run()
    {
        try
        {
            var p = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Check(!AmbientParticles.Enabled(p) && !p.AmbientFog && !p.AmbientLeaves && !p.AmbientDust, "old profiles default off");
            using var field = new AmbientParticles();
            field.Begin(p, 0);
            Check(!field.TrySample(1, 1, 0, AmbientParticleKind.Fog, out _) && field.Samples == 0, "off does no sampling");
            p.AmbientFog = p.AmbientLeaves = p.AmbientDust = true;
            p.AmbientParticleDensity = 100;
            p.AmbientParticleCap = 512;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(p, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Check(restored.AmbientFog && restored.AmbientLeaves && restored.AmbientDust
                && restored.AmbientParticleDensity == 100 && restored.AmbientParticleCap == 512, "all settings persist");
            Check(AmbientParticles.Classify(0x3DEB, false, false, false) == AmbientParticleKind.Fog, "swamp fog");
            Check(AmbientParticles.Classify(0xC4, false, false, false) == AmbientParticleKind.Leaves, "forest leaves");
            Check(AmbientParticles.Classify(3, true, false, false) == AmbientParticleKind.Leaves, "foliage on grass");
            Check(AmbientParticles.Classify(0x16, false, false, false) == AmbientParticleKind.Dust, "sand dust");
            foreach (ushort id in new ushort[] { 0x16, 0xC4, 0x3DEB })
            {
                Check(AmbientParticles.Classify(id, true, true, false) == AmbientParticleKind.None, "roof suppresses all types");
                Check(AmbientParticles.Classify(id, true, false, true) == AmbientParticleKind.None, "wet suppresses all types");
            }
            Check(AmbientParticles.Classify(0xFFFF, false, false, false) == AmbientParticleKind.None, "unknown quiet");
            int[] emitters = new int[3];
            foreach (AmbientParticleKind kind in new[] { AmbientParticleKind.Fog, AmbientParticleKind.Leaves, AmbientParticleKind.Dust })
            {
                field.Begin(p, 2);
                for (int i = 0; i < 2048; i++)
                {
                    if (field.TrySample(i % 64, i / 64, 0, kind, out var particle))
                    {
                        emitters[(int)kind - 1]++;
                        Check(particle.Alpha >= 0 && particle.Alpha <= 0.65f, "bounded opacity");
                        Check(float.IsFinite(particle.X) && float.IsFinite(particle.Y), "finite motion");
                    }
                }
                Check(field.Emitted > 150 && field.Emitted < 350, "expected sparse density");
            }
            // Density is monotonic for fixed tiles, caps clamp corrupt profiles.
            field.Begin(p, 2);
            for (int i = 0; i < 2048; i++) field.TrySample(i % 64, i / 64, 0, AmbientParticleKind.Leaves, out _);
            int high = field.Emitted;
            p.AmbientParticleDensity = 25;
            field.Begin(p, 2);
            for (int i = 0; i < 2048; i++) field.TrySample(i % 64, i / 64, 0, AmbientParticleKind.Leaves, out _);
            Check(field.Emitted > 0 && field.Emitted < high, "density reduces emissions");
            p.AmbientParticleDensity = int.MaxValue;
            p.AmbientParticleCap = 7;
            field.Begin(p, 2);
            for (int i = 0; i < 10000; i++) field.TrySample(i, i, 0, AmbientParticleKind.Fog, out _);
            Check(field.Emitted == 7, "per-frame cap");
            p.AmbientParticleCap = int.MaxValue;
            field.Begin(p, 2);
            for (int i = 0; i < 10000; i++) field.TrySample(i, i, 0, AmbientParticleKind.Fog, out _);
            Check(field.Emitted <= 512 && field.Samples <= 4096, "absolute work limits");
            field.Begin(p, 2);
            for (int i = 0; i < 10000; i++) field.TrySample(i, i, 0, AmbientParticleKind.None, out _);
            Check(field.Samples == 4096 && field.Emitted == 0, "empty-biome sample limit");
            foreach (int invalid in new[] { 0, -1 })
            {
                p.AmbientParticleDensity = invalid;
                Check(!AmbientParticles.Enabled(p), "zero/negative density disables");
                p.AmbientParticleDensity = 100;
                p.AmbientParticleCap = invalid;
                Check(!AmbientParticles.Enabled(p), "zero/negative cap disables");
                p.AmbientParticleCap = 512;
            }
            // Time-based motion is reproducible regardless of intervening frames.
            field.Begin(p, 3);
            AmbientParticles.Particle a = default;
            int emitter = -1;
            for (int x = 0; x < 100; x++)
                if (field.TrySample(x, 0, 0, AmbientParticleKind.Leaves, out a)) { emitter = x; break; }
            Check(emitter >= 0, "found emitter");
            field.Begin(p, 4);
            Check(field.TrySample(emitter, 0, 0, AmbientParticleKind.Leaves, out var b) && a != b, "motion advances");
            field.Begin(p, 3);
            Check(field.TrySample(emitter, 0, 0, AmbientParticleKind.Leaves, out var c) && a == c, "deterministic time");
            foreach (AmbientParticleKind kind in new[] { AmbientParticleKind.Fog, AmbientParticleKind.Leaves, AmbientParticleKind.Dust })
            {
                p.AmbientFog = kind != AmbientParticleKind.Fog;
                p.AmbientLeaves = kind != AmbientParticleKind.Leaves;
                p.AmbientDust = kind != AmbientParticleKind.Dust;
                field.Begin(p, 3);
                for (int x = 0; x < 100; x++) Check(!field.TrySample(x, 0, 0, kind, out _), "individual toggle");
            }
            GD.Print("[Ambient particles] PASS: defaults, persistence, biomes, cover, density, caps, toggles, motion");
            return true;
        }
        catch (Exception e) { GD.PrintErr("[Ambient particles] FAIL: " + e.Message); return false; }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
