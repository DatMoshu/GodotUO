// GUO-owned (ADR-0023): the post-processing proof run.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using GUO.Game;
using GUO.Renderer.PostFx;

namespace GUO.Host;

/// <summary>
/// <c>--postfx-sheet DIR</c>: in the world, prove Classic is untouched, prove
/// the pass pipeline is lossless, then photograph and time every preset and
/// every shader on its own.
/// </summary>
/// <remarks>
/// <para>
/// Classic: with no pass enabled the stack must hand back the world target's
/// own texture (the same object), so nothing about the draw can differ.
/// </para>
/// <para>
/// Lossless pipeline: an identity pass (a shader that writes its source) runs
/// through the whole stack (the SubViewport, BackBufferCopy, blend_disabled,
/// nearest sampling), and its output is compared with the world target pixel
/// by pixel, in the same frame. Any filtering, offset or alpha loss shows here.
/// </para>
/// <para>
/// Cost: each look's GPU time is the post-processing viewport's measured GPU
/// time averaged over 120 frames (RenderingServer's own measurement, so it
/// covers the passes and nothing else).
/// </para>
/// </remarks>
internal static class PostFxProbe
{
    public static bool Passed { get; private set; }

    private const string Identity = @"shader_type canvas_item;
render_mode blend_disabled;
uniform sampler2D source : hint_screen_texture, filter_nearest;
void fragment() { COLOR = texture(source, SCREEN_UV); }";

    public static async System.Threading.Tasks.Task Run(Node host, string dir)
    {
        Directory.CreateDirectory(dir);
        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        if (!Client.Game.UO.World.InGame)
        {
            GD.PrintErr("[GUO] postfx probe: never got into the world.");
            return;
        }

        PostFxStack stack = PostFxStack.Instance;
        var report = new JsonObject();
        bool ok = true;

        // Let chunks load and the camera settle.
        await InputProbe.Wait(host, 150);

        // 1. Classic returns the world target's own texture.
        stack.Split = 0f;
        stack.Use(PostFxPreset.Classic(), remember: false);
        await Frames(host, 3);
        bool classicSame = stack.LastWorld != null && ReferenceEquals(stack.LastOutput, stack.LastWorld.Texture);
        report["classic_is_world_texture"] = classicSame;
        GD.Print($"[GUO] postfx probe: Classic hands back the world texture itself: {classicSame}");
        ok &= classicSame;
        await Snap(host, dir, "00_classic");

        // 2. An identity pass is lossless, compared within one frame.
        PostFxLibrary.Register("__identity", new Shader { Code = Identity });
        var identity = new PostFxPreset { Name = "Identity" };
        identity.Passes.Add(new PostFxPass { Shader = "__identity" });
        stack.Use(identity, remember: false);
        await Frames(host, 6);
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image world = stack.LastWorld.Texture.GetImage();
        Image output = stack.LastOutput.GetImage();
        (long differ, long total) = Compare(world, output);
        report["identity_pass"] = new JsonObject { ["differing_pixels"] = differ, ["pixels"] = total };
        GD.Print($"[GUO] postfx probe: identity pass through the stack: {differ} of {total} pixels differ");
        ok &= differ == 0 && total > 0;
        world.SavePng(Path.Combine(dir, "identity_world.png"));
        output.SavePng(Path.Combine(dir, "identity_output.png"));

        // 3. Every shader alone, at its defaults: the per-pass cost and a frame.
        var shaders = new JsonObject();
        foreach (string name in PostFxLibrary.ShaderNames())
        {
            var one = new PostFxPreset { Name = "pass " + name };
            one.Passes.Add(new PostFxPass { Shader = name });
            stack.Use(one, remember: false);
            double ms = await Gpu(host);
            string file = $"pass_{name}";
            await Snap(host, dir, file);
            shaders[name] = new JsonObject { ["gpu_ms"] = Math.Round(ms, 4), ["png"] = file + ".png" };
            GD.Print($"[GUO] postfx probe: pass {name}: {ms:0.000} ms GPU");
        }

        report["passes"] = shaders;

        // 4. Every preset.
        var presets = new JsonObject();
        int n = 1;
        foreach (PostFxPreset p in PostFxLibrary.Presets())
        {
            if (p.IsClassic)
            {
                continue;
            }

            stack.Use(p, remember: false);
            double ms = await Gpu(host);
            string file = $"{n++:00}_{Slug(p.Name)}";
            await Snap(host, dir, file);
            presets[p.Name] = new JsonObject
            {
                ["gpu_ms"] = Math.Round(ms, 4), ["png"] = file + ".png", ["passes"] = p.Passes.Count,
                ["description"] = p.Description,
            };
            GD.Print($"[GUO] postfx probe: preset {p.Name}: {ms:0.000} ms GPU");
        }

        report["presets"] = presets;

        // 5. The mobile tier: heavy looks at half resolution.
        var tiers = new JsonObject();
        foreach (string name in new[] { "Glow", "Ink Outline" })
        {
            PostFxPreset heavy = PostFxLibrary.Find(name);
            if (heavy == null)
            {
                continue;
            }

            stack.Use(heavy, remember: false);
            stack.FullQuality = true;
            double full = await Gpu(host);
            stack.FullQuality = false;
            double half = await Gpu(host);
            string file = $"tier_half_{Slug(name)}";
            await Snap(host, dir, file);
            tiers[name] = new JsonObject
            {
                ["full_gpu_ms"] = Math.Round(full, 4), ["half_gpu_ms"] = Math.Round(half, 4),
                ["half_scale"] = stack.Scale, ["png"] = file + ".png",
            };
            GD.Print($"[GUO] postfx probe: tier {name}: full {full:0.000} ms, half {half:0.000} ms (scale {stack.Scale})");
            stack.FullQuality = true;
        }

        report["tiers"] = tiers;

        // 6. The A/B split, on one look.
        PostFxPreset noir = PostFxLibrary.Find("Noir");
        if (noir != null)
        {
            stack.Use(noir, remember: false);
            stack.Split = 0.5f;
            stack.Rebuild();
            await Frames(host, 20);
            await Snap(host, dir, "zz_split_noir");

            // The menu, open on that look, with the split on.
            PostFxMenu.Toggle();
            await Frames(host, 20);
            await Snap(host, dir, "zz_menu");
            PostFxMenu.Toggle();
            await Frames(host, 5);
            stack.Split = 0f;
        }

        stack.Use(PostFxPreset.Classic(), remember: false);
        var vp = Client.Game.GetViewport().GetVisibleRect().Size;
        report["window"] = $"{vp.X}x{vp.Y}";
        report["world_target"] = stack.LastWorld != null ? $"{stack.LastWorld.Width}x{stack.LastWorld.Height}" : null;
        report["renderer"] = RenderingServer.GetVideoAdapterName() + " / " + ProjectSettings.GetSetting("rendering/renderer/rendering_method");
        File.WriteAllText(Path.Combine(dir, "report.json"), report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[GUO] postfx probe: {(ok ? "ok" : "FAIL")}; report {Path.Combine(dir, "report.json")}");
        Passed = ok;
    }

    private static async System.Threading.Tasks.Task Frames(Node host, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async System.Threading.Tasks.Task<double> Gpu(Node host)
    {
        await Frames(host, 20);
        double sum = 0;
        const int N = 120;
        for (int i = 0; i < N; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            sum += PostFxStack.Instance.LastGpuMs;
        }

        return sum / N;
    }

    private static async System.Threading.Tasks.Task Snap(Node host, string dir, string name)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image frame = host.GetViewport().GetTexture().GetImage();
        frame.SavePng(Path.Combine(dir, name + ".png"));
    }

    private static (long differ, long total) Compare(Image a, Image b)
    {
        if (a == null || b == null || a.GetSize() != b.GetSize())
        {
            return (-1, 0);
        }

        a.Convert(Image.Format.Rgba8);
        b.Convert(Image.Format.Rgba8);
        byte[] x = a.GetData(), y = b.GetData();
        long differ = 0;
        for (int i = 0; i < x.Length; i += 4)
        {
            if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2] || x[i + 3] != y[i + 3])
            {
                differ++;
            }
        }

        return (differ, x.Length / 4);
    }

    private static string Slug(string s)
    {
        var chars = new List<char>();
        foreach (char c in s.ToLowerInvariant())
        {
            chars.Add(char.IsLetterOrDigit(c) ? c : '_');
        }

        return new string(chars.ToArray()).Trim('_');
    }
}
