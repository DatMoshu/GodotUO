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
/// every shader on its own. With <c>--postfx-tour</c>, instead a live tour
/// for recording (see <see cref="Tour"/>).
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

    public static async System.Threading.Tasks.Task Run(Node host, string dir, bool tour = false)
    {
        // A device run names a user:// folder (the app's own files, pulled with run-as).
        if (dir.StartsWith("user://"))
        {
            dir = ProjectSettings.GlobalizePath(dir);
        }

        Directory.CreateDirectory(dir);

        // With --autologin (a device run: the probe's clicks are desktop
        // coordinates) the client logs itself in; wait for it before clicking.
        for (int i = 0; i < 60 * 90 && !Client.Game.UO.World.InGame; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (i == 60 * 5 && Client.Game.Scene is Game.Scenes.LoginScene login && !login.CanAutologin)
            {
                break;
            }
        }

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

        if (tour)
        {
            Passed = await Tour(host, dir);
            return;
        }

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
        // The quality is the player's setting and is saved; put it back after.
        bool fullQualityWas = stack.FullQuality;
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

        stack.FullQuality = fullQualityWas;
        report["tiers"] = tiers;

        // 6. The object-id buffer: off unless a pass reads it, then one id per object.
        stack.Use(PostFxPreset.Classic(), remember: false);
        await Frames(host, 5);
        bool idOff = !stack.IdCanvas.IsValid;
        PostFxPreset sil = PostFxLibrary.Find("Silhouettes");
        var idReport = new JsonObject { ["off_without_id_pass"] = idOff };
        if (sil != null)
        {
            stack.Use(sil, remember: false);
            double ms = await Gpu(host);
            await Snap(host, dir, "silhouettes");
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image ids = stack.IdTexture?.GetImage();
            int distinct = 0;
            if (ids != null)
            {
                ids.SavePng(Path.Combine(dir, "silhouettes_ids.png"));
                ids.Convert(Image.Format.Rgba8);
                byte[] d = ids.GetData();
                var seen = new HashSet<int>();
                for (int i = 0; i < d.Length; i += 4)
                {
                    seen.Add(d[i] << 16 | d[i + 1] << 8 | d[i + 2]);
                }

                distinct = seen.Count;
            }

            idReport["gpu_ms"] = Math.Round(ms, 4);
            idReport["distinct_ids_on_screen"] = distinct;
            GD.Print($"[GUO] postfx probe: id buffer off without an id pass: {idOff}; Silhouettes {ms:0.000} ms, {distinct} distinct ids on screen");
            ok &= idOff && distinct > 1;
        }

        report["object_ids"] = idReport;

        // 7. The A/B split, on one look.
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

        // 8. Ctrl+Shift+E opens the menu, unless a macro has the key: then it
        // is the macro's, as in ClassicUO (review P3).
        bool freeOpens = await HotkeyOpens(host);
        var bound = Game.Managers.Macro.CreateEmptyMacro("GUO postfx probe macro");
        bound.Key = GUO.Platform.Sdl.SDL.SDL_Keycode.SDLK_E;
        bound.Ctrl = true;
        bound.Shift = true;
        Client.Game.UO.World.Macros.PushToBack(bound);
        bool boundOpens = await HotkeyOpens(host);
        Client.Game.UO.World.Macros.Remove(bound);
        report["hotkey"] = new JsonObject { ["opens_when_free"] = freeOpens, ["opens_when_a_macro_has_it"] = boundOpens };
        GD.Print($"[GUO] postfx probe: Ctrl+Shift+E opens the menu when free: {freeOpens}; when a macro has it: {boundOpens}");
        ok &= freeOpens && !boundOpens;

        stack.Use(PostFxPreset.Classic(), remember: false);
        var vp = Client.Game.GetViewport().GetVisibleRect().Size;
        report["window"] = $"{vp.X}x{vp.Y}";
        report["world_target"] = stack.LastWorld != null ? $"{stack.LastWorld.Width}x{stack.LastWorld.Height}" : null;
        report["renderer"] = RenderingServer.GetVideoAdapterName() + " / " + ProjectSettings.GetSetting("rendering/renderer/rendering_method");
        File.WriteAllText(Path.Combine(dir, "report.json"), report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[GUO] postfx probe: {(ok ? "ok" : "FAIL")}; report {Path.Combine(dir, "report.json")}");
        Passed = ok;
    }

    /// <summary>Press Ctrl+Shift+E as a keyboard would; whether the menu opened (it is closed again after).</summary>
    private static async System.Threading.Tasks.Task<bool> HotkeyOpens(Node host)
    {
        foreach (bool pressed in new[] { true, false })
        {
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                Keycode = Key.E, PhysicalKeycode = Key.E, Pressed = pressed, CtrlPressed = true, ShiftPressed = true,
            });
        }

        await Frames(host, 5);
        bool open = PostFxMenu.IsOpen;
        if (open)
        {
            PostFxMenu.Toggle();
            await Frames(host, 3);
        }

        return open;
    }

    /// <summary>
    /// The live tour, for a recording (<c>tools/postfx/run.py tour</c> runs it
    /// under Godot's --write-movie): walk the four diagonals while the look
    /// switches every 75 frames, then open the menu on Noir with Compare on and
    /// move the split slider, then the vignette radius slider. The sliders are
    /// moved by setting their value, as a drag does, not by mouse events.
    /// tour.json lists each beat's frame (Engine.GetFramesDrawn, which is the
    /// movie's frame index), so a video can be cut and captioned from it.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> Tour(Node host, string dir)
    {
        var beats = new JsonArray();
        void Beat(string what)
        {
            int frame = Engine.GetFramesDrawn();
            beats.Add(new JsonObject { ["frame"] = frame, ["beat"] = what });
            GD.Print($"[GUO] postfx tour: {what} at frame {frame}");
        }

        PostFxStack stack = PostFxStack.Instance;
        stack.Split = 0f;
        stack.Use(PostFxPreset.Classic(), remember: false);
        await Frames(host, 30);
        Beat("Classic");
        System.Threading.Tasks.Task walk = InputProbe.WalkAround(host);
        string[] looks = { "Sepia", "Ink Outline", "Glow", "Teal & Orange", "Cel" };
        int k = 0;
        while (!walk.IsCompleted)
        {
            await Frames(host, 75);
            if (!walk.IsCompleted && k < looks.Length && PostFxLibrary.Find(looks[k]) is PostFxPreset p)
            {
                stack.Use(p, remember: false);
                Beat(looks[k]);
            }

            k++;
        }

        await walk;
        Beat("walk end");

        stack.Use(PostFxLibrary.Find("Noir"), remember: false);
        stack.Split = 0.5f;
        stack.Rebuild();
        PostFxMenu.Toggle();
        await Frames(host, 20);
        Beat("menu open, Noir, Compare with Classic");
        var sliders = new List<HSlider>();
        void Collect(Node n)
        {
            if (n is HSlider s && s.IsVisibleInTree())
            {
                sliders.Add(s);
            }

            foreach (Node c in n.GetChildren())
            {
                Collect(c);
            }
        }

        Collect(host.GetTree().Root);
        HSlider split = sliders.Find(s => Math.Abs(s.Value - 0.5) < 1e-6 && Math.Abs(s.MaxValue - 0.95) < 1e-6);
        HSlider radius = sliders.Find(s => Math.Abs(s.Value - 0.75) < 1e-6);
        GD.Print($"[GUO] postfx tour: {sliders.Count} sliders, split {split != null}, radius {radius != null}");
        await Sweep(host, split, new[] { 0.5, 0.2, 0.8, 0.5 }, 45);
        Beat("vignette radius slider");
        await Sweep(host, radius, new[] { 0.75, 0.35, 1.0, 0.75 }, 40);
        await Frames(host, 30);
        Beat("end");
        PostFxMenu.Toggle();
        stack.Split = 0f;
        stack.Use(PostFxPreset.Classic(), remember: false);
        await Frames(host, 5);
        File.WriteAllText(Path.Combine(dir, "tour.json"), new JsonObject { ["beats"] = beats }.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return split != null && radius != null;
    }

    private static async System.Threading.Tasks.Task Sweep(Node host, HSlider s, double[] stops, int framesPerLeg)
    {
        if (s == null)
        {
            return;
        }

        for (int i = 1; i < stops.Length; i++)
        {
            for (int f = 1; f <= framesPerLeg; f++)
            {
                double t = (double)f / framesPerLeg;
                t = t * t * (3 - 2 * t);
                s.Value = stops[i - 1] + (stops[i] - stops[i - 1]) * t;
                await Frames(host, 1);
            }
        }
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
