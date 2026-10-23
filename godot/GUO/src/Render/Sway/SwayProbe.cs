// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Renderer.Sway;

public partial class SwayProbe : Node
{
    public override void _Ready()
    {
        if (!string.IsNullOrEmpty(OS.GetEnvironment("GUO_SWAY_VISUAL"))) { RunVisual(); return; }
        try
        {
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Check(SwayRender.Strength(profile, 1) == 0, "default off, including rain");
            UoPlusOptions.Set(profile, UoPlusFeature.Sway, true, 50);
            profile = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Check(SwayRender.Strength(profile, 0) == 0.5f && SwayRender.Strength(profile, 1) == 1.25f, "persisted density and rain amplitude");
            Check(SwayRender.Strength(profile, 9) == 1.25f, "rain clamps");
            UoPlusOptions.Set(profile, UoPlusFeature.Sway, true, 0);
            Check(SwayRender.Strength(profile, 1) == 0, "zero density skips work");
            UoPlusOptions.ResetDefaults(profile);
            Check(!UoPlusOptions.Requested(profile, UoPlusFeature.Sway), "Defaults off");
            Check(SwayRender.Classify(0x0CCE) == 1 && SwayRender.Classify(0x0CAC) == 2
                && SwayRender.Classify(0x0C83) == 3 && SwayRender.Classify(0x0CB9) == 4
                && SwayRender.Classify(0x0A1A) == 5, "all catalogue categories");
            foreach (int id in new[] { -1, 0, 0x0190, 0x0006, 0x0550, 0x0BD2, 0x0E59, 0x0CC9, 0x0CCA, 0x0CD0, 0x0CDA, 0x11C7, 65536 })
                Check(SwayRender.Classify(id) == 0, "safe default, wall, roof, sign, character and trunk ids");
            Check(SwayRender.Weight(1, 1) == 0 && SwayRender.Weight(0.75f, 1) == 0
                && SwayRender.Weight(0, 1) == 1, "fixed trunk and flexible crown");
            Check(SwayRender.Weight(0, 5) == 0 && SwayRender.Weight(1, 5) == 1, "hanging top stays fixed");
            Check(SwayRender.Phase(633, 858) == SwayRender.Phase(633, 858)
                && SwayRender.Phase(633, 858) != SwayRender.Phase(634, 858), "stable world-position phase");
            Check(WeatherSignal.RainIntensity(WeatherType.WT_RAIN, 35) == 0.5f
                && WeatherSignal.RainIntensity(WeatherType.WT_STORM_BREWING, 70) == 1
                && WeatherSignal.RainIntensity(WeatherType.WT_SNOW, 70) == 0
                && WeatherSignal.RainIntensity(null) == 0, "shared weather input");
            Check(SwayRender.Parse("{\"version\":1,\"ranges\":[]}")[0x0CCA] == 0, "empty data never guesses");
            bool rejected = false;
            try { SwayRender.Parse("{\"version\":2,\"ranges\":[]}"); } catch (FormatException) { rejected = true; }
            Check(rejected, "unknown schema rejected");
            using var mesh = UltimaBatcher2D.MakeSwayMesh(Vector2.Zero, Vector2.One, new Vector2(64, 128), 1, 3);
            var arrays = mesh.SurfaceGetArrays(0);
            Check(arrays[(int)Mesh.ArrayType.Vertex].AsVector2Array().Length == 24, "four bend strips");
            var weights = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            Check(weights[0] == 1 && weights[^4] == 0, "shader receives fixed-anchor weights");
            GD.Print("[sway] PASS: catalogue, safe defaults, static categories, anchors, world phase, persistence, zero/Defaults, rain and mesh payload");
            GetTree().Quit(0);
        }
        catch (Exception e) { GD.PrintErr("[sway] FAIL: " + e); GetTree().Quit(1); }
    }

    private async void RunVisual()
    {
        SubViewport viewport = null;
        UltimaBatcher2D batcher = null;
        try
        {
            string folder = OS.GetEnvironment("GUO_SWAY_VISUAL");
            System.IO.Directory.CreateDirectory(folder);
            viewport = new SubViewport { Size = new Vector2I(256, 160), TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            AddChild(viewport);
            var canvas = new Node2D { TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
            viewport.AddChild(canvas);
            batcher = new UltimaBatcher2D(canvas.GetCanvasItem());
            using var pattern = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
            for (int y = 0; y < 96; y++) for (int x = 0; x < 32; x++)
                pattern.SetPixel(x + 96, y + 64, (x + y) % 5 == 0 ? Colors.Yellow : Colors.DarkGreen);
            using var texture = ImageTexture.CreateFromImage(pattern);
            async System.Threading.Tasks.Task<Image> Frame(bool enabled, uint ticks)
            {
                GUO.Time.Ticks = ticks;
                batcher.BeginFrame();
                batcher.Begin();
                UltimaBatcher2D.BatchedWorld = true;
                batcher.SetWorldOffset(1000, 2000);
                batcher.StaticSway = enabled ? new SwayInstance(1, 0, 1) : default;
                batcher.Draw(texture, new Vector2(1032, 2024), new GUO.Compat.Rectangle(96, 64, 32, 96),
                    new Vector3(0, 0, 1), 0, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);
                batcher.StaticSway = default;
                batcher.Draw(texture, new Vector2(1128, 2024), new GUO.Compat.Rectangle(96, 64, 32, 96),
                    new Vector3(0, 0, 1), 0, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);
                batcher.End();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                return viewport.GetTexture().GetImage();
            }
            using var baseline = await Frame(false, 0);
            using var neutral = await Frame(true, 0);
            baseline.SavePng(System.IO.Path.Combine(folder, "sway-native.png"));
            neutral.SavePng(System.IO.Path.Combine(folder, "sway-neutral-mesh.png"));
            Check(baseline.GetData().AsSpan().SequenceEqual(neutral.GetData()), "zero-displacement mesh preserves native colour, alpha, UV and nearest pixels");
            using var moving = await Frame(true, 1000);
            int changed = 0;
            for (int y = 0; y < 160; y++) for (int x = 0; x < 256; x++)
            {
                if (baseline.GetPixel(x, y) == moving.GetPixel(x, y)) continue;
                changed++;
                Check(x < 100 && y < 92, "base and neighboring non-static pixels remain fixed");
            }
            Check(changed > 0 && batcher.SwayDraws == 1, "shader moves classified crown only");
            moving.SavePng(System.IO.Path.Combine(folder, "sway-moving.png"));
            GD.Print($"[sway visual] PASS: translated world canvas and atlas; zero-displacement mesh 0 changed pixels; moving crown {changed} changed pixels; fixed base and neighbor");
            GetTree().Quit(0);
        }
        catch (Exception e) { GD.PrintErr("[sway visual] FAIL: " + e); GetTree().Quit(1); }
        finally { batcher?.Dispose(); viewport?.QueueFree(); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
