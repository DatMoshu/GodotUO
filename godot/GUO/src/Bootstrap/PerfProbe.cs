// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Game;
using GUO.Renderer;

namespace GUO.Host;

/// <summary>
/// Frame time in five fixed scenes (Epic B, B1): the login screen, an open
/// field, the Britain bank, a dense forest and a dungeon. Per scene it reports
/// the mean, p95 and p99 wall-clock frame time with vsync and the frame cap
/// off, draw calls, and the batcher's own counts -- canvas items opened (each
/// is a batch break), texture switches and draw commands -- next to the
/// client's profiler phases. Written as a markdown table and JSON into
/// <c>--perf-out</c> (launchers\dev\perf_probe.bat puts it in build\perf).
/// </summary>
/// <remarks>
/// The scenes are reached with GM <c>[go</c> commands, so any shard where the
/// probe account is a GM will do, on any platform the client runs on: a device
/// owner runs it with the same flags. The frame times are only comparable
/// between runs on one machine at one window size; the draw calls and batcher
/// counts are comparable anywhere.
/// </remarks>
internal static class PerfProbe
{
    public static bool Passed { get; private set; }

    private sealed record Scene(string Name, string Go, string What);

    // Places also used by tools\ab_compare where they overlap, so a scene can be
    // photographed there and timed here.
    private static readonly Scene[] Scenes =
    {
        new("open-field", "[go 1164 1668", "Felucca grassland south of Britain: land and a few trees"),
        new("britain-bank", "[go 1434 1699 0", "the Britain bank: dense statics, roofs, vendors and passers-by"),
        new("dense-forest", "[go 633 858", "the forest east of Yew (ab_compare yew-forest)"),
        new("dungeon", "[go 5401 629", "the mouth of Despise (ab_compare despise-mouth): dungeon light level, torches"),
    };

    private const int Settle = 180;
    private const int Frames = 360;

    public static async System.Threading.Tasks.Task Run(Node host, string outDir, string label, float zoom = 0)
    {
        var results = new List<Dictionary<string, object>>();
        Rid viewport = host.GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        bool profiling = GUO.Utility.Profiler.Enabled;
        GUO.Utility.Profiler.Enabled = true;

        // The login screen, before anything logs in.
        for (int i = 0; i < 600 && Game.Managers.UIManager.GetGump<Game.UI.Gumps.Login.LoginGump>() == null; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await InputProbe.Wait(host, Settle);
        results.Add(await Measure(host, viewport, "login", "the login screen over the background"));

        await InputProbe.EnterTheWorld(host, 200);
        World world = Client.Game.UO.World;
        if (!world.InGame)
        {
            GD.PrintErr("[GUO] perf probe: never got into the world.");
            GUO.Utility.Profiler.Enabled = profiling;
            Write(outDir, label, results);
            return;
        }

        // --perf-zoom Z: the camera at Z (clamped to its range) for every world
        // scene. Zoomed out is where the sorted world costs most (ADR-0007).
        if (zoom > 0)
        {
            var camera = Client.Game.Scene.Camera;
            camera.Zoom = System.Math.Clamp(zoom, camera.ZoomMin, camera.ZoomMax);
            GD.Print($"[GUO] perf probe: zoom {camera.Zoom:F1} (asked {zoom:F1}, range {camera.ZoomMin:F1}-{camera.ZoomMax:F1})");
        }

        foreach (Scene scene in Scenes)
        {
            await InputProbe.Say(host, scene.Go);
            // Chunks newly in view load and build their meshes first; that is a
            // one-off, not what a player standing still pays every frame.
            await InputProbe.Wait(host, Settle);
            results.Add(await Measure(host, viewport, scene.Name, scene.What));
        }

        GUO.Utility.Profiler.Enabled = profiling;
        Write(outDir, label, results);
        Passed = results.Count == Scenes.Length + 1;
    }

    private static async System.Threading.Tasks.Task<Dictionary<string, object>> Measure(Node host, Rid viewport, string name, string what)
    {
        // Uncapped: with vsync or a frame cap every frame below the cap reads as
        // the cap, and a renderer change that saves 4 ms would not show.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        int maxFps = Engine.MaxFps;
        Engine.MaxFps = 0;
        // Upstream's own pacing (GameController._intervalFixedUpdate) skips the
        // draw when a frame comes early: one draw per 1000/FPS ms, and one per
        // 217 ms while the window is inactive -- which a probe window, never
        // focused, always is. Left alone, most measured frames draw nothing.
        // Made tiny for the measurement, so every frame draws; put back after.
        var field = typeof(GUO.GameController).GetField("_intervalFixedUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        float[] interval = field?.GetValue(Client.Game) as float[];
        float[] kept = interval?.ToArray();
        if (interval != null)
        {
            // Not zero: upstream wraps the elapsed time with %=, and x % 0 is NaN,
            // which would stop drawing for good.
            System.Array.Fill(interval, 0.001f);
        }

        await InputProbe.Wait(host, 30);
        long drawsBefore = UltimaBatcher2D.FramesBegun;

        var ms = new double[Frames];
        double calls = 0, items = 0, switches = 0, commands = 0, prepare = 0, world = 0, cpu = 0, gpu = 0, objects = 0;
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Frames; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            ms[i] = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            last = now;
            calls += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            objects += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
            (int sw, int it, int cm) = UltimaBatcher2D.LastFrame;
            switches += sw;
            items += it;
            commands += cm;
            prepare += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE).LastTime;
            world += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD).LastTime;
            cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
        }

        long draws = UltimaBatcher2D.FramesBegun - drawsBefore;
        if (interval != null)
        {
            System.Array.Copy(kept, interval, kept.Length);
        }

        Engine.MaxFps = maxFps;
        double[] sorted = ms.OrderBy(v => v).ToArray();
        double P(double q) => sorted[System.Math.Min(sorted.Length - 1, (int)System.Math.Ceiling(q * sorted.Length) - 1)];
        var player = Client.Game.UO.World?.Player;
        var r = new Dictionary<string, object>
        {
            ["scene"] = name,
            ["what"] = what,
            ["at"] = player != null ? $"{player.X},{player.Y},{player.Z} map {Client.Game.UO.World.MapIndex}" : "",
            ["frames"] = Frames,
            ["draws_per_frame"] = System.Math.Round((double)draws / Frames, 2),
            ["mean_ms"] = System.Math.Round(ms.Average(), 3),
            ["p95_ms"] = System.Math.Round(P(0.95), 3),
            ["p99_ms"] = System.Math.Round(P(0.99), 3),
            ["fps"] = System.Math.Round(1000.0 / ms.Average(), 1),
            ["draw_calls"] = System.Math.Round(calls / Frames, 1),
            ["canvas_objects"] = System.Math.Round(objects / Frames, 1),
            ["batcher_items"] = System.Math.Round(items / Frames, 1),
            ["texture_switches"] = System.Math.Round(switches / Frames, 1),
            ["draw_commands"] = System.Math.Round(commands / Frames, 1),
            ["world_prepare_ms"] = System.Math.Round(prepare / Frames, 3),
            ["world_draw_ms"] = System.Math.Round(world / Frames, 3),
            ["render_cpu_ms"] = System.Math.Round(cpu / Frames, 3),
            ["render_gpu_ms"] = System.Math.Round(gpu / Frames, 3),
        };
        GD.Print($"[GUO] perf probe: {name}: mean {r["mean_ms"]} ms, p95 {r["p95_ms"]}, p99 {r["p99_ms"]}, "
                 + $"{r["draw_calls"]} draw calls, {r["batcher_items"]} batcher items, {r["texture_switches"]} texture switches, "
                 + $"{r["draw_commands"]} commands");
        return r;
    }

    private static void Write(string outDir, string label, List<Dictionary<string, object>> results)
    {
        string dir = string.IsNullOrWhiteSpace(outDir) ? ProjectSettings.GlobalizePath("user://perf") : outDir;
        Directory.CreateDirectory(dir);
        string stem = string.IsNullOrWhiteSpace(label) ? "perf" : $"perf_{label}";
        Vector2I size = DisplayServer.WindowGetSize();
        var md = new StringBuilder();
        md.AppendLine($"# Frame time: {label}");
        md.AppendLine();
        md.AppendLine($"{System.DateTime.Now:yyyy-MM-dd HH:mm}, {OS.GetName()}, {RenderingServer.GetVideoAdapterName()}, "
                      + $"window {size.X}x{size.Y}, zoom {Client.Game?.Scene?.Camera?.Zoom:F1}, vsync and frame cap off, "
                      + $"{Frames} frames per scene after {Settle} to settle.");
        md.AppendLine();
        md.AppendLine("| Scene | mean ms | p95 ms | p99 ms | FPS | draws/frame | draw calls | batcher items | texture switches | draw commands | world prepare ms | world draw ms | render CPU ms | GPU ms |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            md.AppendLine($"| {r["scene"]} | {r["mean_ms"]} | {r["p95_ms"]} | {r["p99_ms"]} | {r["fps"]} | {r["draws_per_frame"]} | {r["draw_calls"]} | "
                          + $"{r["batcher_items"]} | {r["texture_switches"]} | {r["draw_commands"]} | {r["world_prepare_ms"]} | "
                          + $"{r["world_draw_ms"]} | {r["render_cpu_ms"]} | {r["render_gpu_ms"]} |");
        }

        md.AppendLine();
        md.AppendLine("Batcher items: canvas items the batcher opened (each is a batch break). Draw commands: sprites, meshes and triangle lists added.");
        File.WriteAllText(Path.Combine(dir, stem + ".md"), md.ToString());
        File.WriteAllText(Path.Combine(dir, stem + ".json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["label"] = label, ["window"] = $"{size.X}x{size.Y}", ["scenes"] = results },
                new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[GUO] perf probe: wrote {Path.Combine(dir, stem + ".md")}");
    }
}
