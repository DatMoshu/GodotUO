// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game;

namespace GUO.Host;

/// <summary>
/// Time the world at each zoom level, from close in to as far out as the
/// camera goes.
/// </summary>
/// <remarks>
/// Zooming out was reported to cost frame rate, and nobody had a number for
/// how much, or for where it goes. Zooming out puts more of the world on
/// screen, so everything that scales with what is visible scales with it:
/// preparing the render lists (RENDER_FRAME_WORLD_PREPARE), sorting and
/// drawing them (RENDER_FRAME_WORLD), and what the renderer then does with
/// the canvas items the batcher built. Each is read separately so the one that
/// grows can be told from the ones that merely follow it.
///
/// Like EffectsProbe it reads the client's own profiler rather than wall
/// clock: the window is vsynced, so frame time says nothing until it is
/// already past sixteen milliseconds.
/// </remarks>
internal static class ZoomProbe
{
    public static bool Passed { get; private set; }

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] zoom probe: never got into the world.");

            return;
        }

        Rid viewport = host.GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);

        bool profiling = GUO.Utility.Profiler.Enabled;
        GUO.Utility.Profiler.Enabled = true;

        var camera = Client.Game.Scene.Camera;
        float original = camera.Zoom;

        // Every step from the closest the camera allows to the farthest.
        int steps = (int)System.Math.Round((camera.ZoomMax - camera.ZoomMin) / camera.ZoomStep);

        for (int i = 0; i <= steps; i += 2)
        {
            camera.Zoom = camera.ZoomMin + i * camera.ZoomStep;

            // Chunks newly in view load and build their meshes first; that is
            // a one-off, not what a player standing still pays every frame.
            await InputProbe.Wait(host, 90);

            GD.Print($"[GUO] zoom probe: {await Measure(host, viewport, camera.Zoom)}");
        }

        camera.Zoom = original;
        GUO.Utility.Profiler.Enabled = profiling;
        Passed = true;
    }

    private static async System.Threading.Tasks.Task<string> Measure(Node host, Rid viewport, float zoom)
    {
        const int Frames = 240;
        double prepare = 0, draw = 0, drawWorst = 0, process = 0, cpu = 0, gpu = 0;
        double update = 0, frame = 0, ui = 0;
        double calls = 0, items = 0;

        System.TimeSpan pausedBefore = System.GC.GetTotalPauseDuration();
        long allocatedBefore = System.GC.GetTotalAllocatedBytes();
        int gen0Before = System.GC.CollectionCount(0), gen2Before = System.GC.CollectionCount(2);

        // Wall clock across the whole run: what a player sees, including
        // the canvas rendering none of the counters below reaches.
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        for (int i = 0; i < Frames; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

            prepare += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE).LastTime;
            double w = GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD).LastTime;
            draw += w;
            drawWorst = System.Math.Max(drawWorst, w);
            update += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.UPDATE_WORLD).LastTime;
            frame += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME).LastTime;
            ui += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_UI).LastTime;
            process += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
            cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
            calls += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            items += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        }

        double wall = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency / Frames;
        double paused = (System.GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
        double allocated = (System.GC.GetTotalAllocatedBytes() - allocatedBefore) / 1024.0;
        int gen0 = System.GC.CollectionCount(0) - gen0Before, gen2 = System.GC.CollectionCount(2) - gen2Before;

        (int world, int covering, int tiles) = Counts();

        return $"zoom {zoom:F1}: {wall:F2} ms a frame ({1000.0 / wall:F0} FPS); prepare {prepare / Frames:F2} ms, world draw {draw / Frames:F2} ms "
            + $"(worst {drawWorst:F2}), whole frame {frame / Frames:F2} ms, UI {ui / Frames:F2} ms, "
            + $"world update {update / Frames:F2} ms, process {process / Frames:F2} ms, render CPU {cpu / Frames:F2} ms, "
            + $"GPU {gpu / Frames:F2} ms, {calls / Frames:F0} draw calls, {items / Frames:F0} objects; "
            + $"sorted {world}, covering land {covering}, loose land {tiles}; "
            + $"GC {paused / Frames:F2} ms a frame, {allocated / Frames:F0} KB allocated a frame, {gen0} gen0 / {gen2} gen2 in {Frames} frames";
    }

    /// <summary>
    /// How much went into each of the render lists last frame, read through
    /// reflection as RenderDump does, so the lists stay private to the scene.
    /// </summary>
    private static (int World, int Covering, int Tiles) Counts()
    {
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        object lists = typeof(Game.Scenes.GameScene)
            .GetField("_renderLists", Private)
            ?.GetValue(Client.Game.GetScene<Game.Scenes.GameScene>());

        if (lists == null)
        {
            return (-1, -1, -1);
        }

        int Count(string field)
        {
            object list = lists.GetType().GetField(field, Private)?.GetValue(lists);

            return list?.GetType().GetProperty("Count")?.GetValue(list) is int n ? n : -1;
        }

        return (Count("_world"), Count("_covering"), Count("_tiles") + Count("_stretchedTiles"));
    }
}
