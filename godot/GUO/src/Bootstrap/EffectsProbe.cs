// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game;
using GUO.Game.Data;

namespace GUO.Host;

/// <summary>
/// Time the world with a crowd of blended effects on screen, and without.
/// </summary>
/// <remarks>
/// Every effect drawn with a blend other than the default makes the batcher
/// cut a back-buffer copier and a new canvas item (ADR-0003), and nobody had
/// measured what that costs in a spell-heavy scene. This puts the character in
/// the world, times a stretch of frames standing still, then fills the screen
/// around it with effects in every blend mode the effect view knows and times
/// the same stretch again.
///
/// The effects are made on this side, through the same World.SpawnEffect the
/// 0x70/0xC0 packet handlers call, so what is drawn is exactly what a shard's
/// effects would draw -- without scripting twenty spell casts and their
/// targets on the shard.
///
/// The window is vsynced, so wall-clock frame time says little until it is
/// already too late. Godot's own _Process monitor turned out too noisy to
/// read a millisecond from (a baseline of ~15 ms that swung by 3 ms between
/// identical runs), so the number that decides is the client's own profiler:
/// the time GameScene spends drawing the world, which is where the batcher
/// builds its canvas items. The baseline is measured before and after the
/// effects, so its drift is visible next to the difference.
/// </remarks>
internal static class EffectsProbe
{
    public static bool Passed { get; private set; }

    public static int Count { get; set; } = 40;

    /// <summary>
    /// Draw the same effects with no blend at all, so the blend states' own
    /// cost can be told apart from the cost of updating and drawing effects.
    /// </summary>
    public static bool Plain { get; set; }

    private static readonly GraphicEffectBlendMode[] Modes =
    {
        GraphicEffectBlendMode.Multiply,
        GraphicEffectBlendMode.Screen,
        GraphicEffectBlendMode.ScreenLess,
        GraphicEffectBlendMode.NormalHalfTransparent,
        GraphicEffectBlendMode.ShadowBlue,
    };

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        // Already in when --shard-command ran first: a probe can be sent
        // somewhere before it starts, which a character made a moment ago
        // in an empty corner of the map needs.
        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] effects probe: never got into the world.");

            return;
        }

        Rid viewport = host.GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);

        await InputProbe.Wait(host, 120);

        bool profiling = GUO.Utility.Profiler.Enabled;
        GUO.Utility.Profiler.Enabled = true;
        await InputProbe.Wait(host, 2);

        var none = await Measure(host, viewport, world, 0);
        var some = await Measure(host, viewport, world, Count);
        var after = await Measure(host, viewport, world, 0);

        GUO.Utility.Profiler.Enabled = profiling;

        GD.Print($"[GUO] effects probe: blends {(Plain ? "none (--effects-plain)" : string.Join(", ", Modes))}");
        GD.Print($"[GUO] effects probe: {none}");
        GD.Print($"[GUO] effects probe: {some}");
        GD.Print($"[GUO] effects probe: {after}");

        double baseWorld = (none.World + after.World) / 2;
        GD.Print(
            $"[GUO] effects probe: {Count} effects cost {some.World - baseWorld:F3} ms drawing the world "
            + $"(baseline {none.World:F3} / {after.World:F3}), "
            + $"{some.Process - (none.Process + after.Process) / 2:F3} ms process, "
            + $"{some.RenderGpu - (none.RenderGpu + after.RenderGpu) / 2:F3} ms render GPU per frame"
        );

        Passed = some.Frames > 0;
    }

    private readonly record struct Sample(
        int Effects, int Frames, double World, double WorldWorst, double Process, double ProcessWorst,
        double RenderCpu, double RenderGpu, int Drawn)
    {
        public override string ToString() =>
            $"{Effects,3} effects ({Drawn} alive): world draw {World:F3} ms avg / {WorldWorst:F3} worst, process {Process:F3} ms avg / {ProcessWorst:F3} worst, "
            + $"render CPU {RenderCpu:F3} ms, GPU {RenderGpu:F3} ms, over {Frames} frames";
    }

    private static async System.Threading.Tasks.Task<Sample> Measure(
        Node host, Rid viewport, World world, int count)
    {
        const int FramesPerRun = 600;
        const int Respawn = 120;

        double process = 0, worst = 0, cpu = 0, gpu = 0, draw = 0, drawWorst = 0;
        int alive = 0;

        for (int i = 0; i < FramesPerRun; i++)
        {
            if (count > 0 && i % Respawn == 0)
            {
                Spawn(world, count);
                await InputProbe.Wait(host, 2);
            }

            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

            double w = GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD).LastTime;
            draw += w;
            drawWorst = System.Math.Max(drawWorst, w);

            double p = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
            process += p;
            worst = System.Math.Max(worst, p);
            cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);

            if (i == FramesPerRun / 2)
            {
                alive = CountEffects(world);
            }
        }

        return new Sample(count, FramesPerRun, draw / FramesPerRun, drawWorst, process / FramesPerRun, worst,
            cpu / FramesPerRun, gpu / FramesPerRun, alive);
    }

    private static void Spawn(World world, int count)
    {
        var player = world.Player;
        int side = (int)System.Math.Ceiling(System.Math.Sqrt(count));

        for (int n = 0; n < count; n++)
        {
            int dx = n % side - side / 2;
            int dy = n / side - side / 2;

            world.SpawnEffect(
                GraphicEffectType.FixedXYZ,
                0,
                0,
                0x36BD, // explosion
                0,
                (ushort)(player.X + dx),
                (ushort)(player.Y + dy),
                player.Z,
                (ushort)(player.X + dx),
                (ushort)(player.Y + dy),
                player.Z,
                10,
                30,
                false,
                false,
                false,
                Plain ? GraphicEffectBlendMode.Normal : Modes[n % Modes.Length]
            );
        }
    }

    private static int CountEffects(World world)
    {
        // The manager is private to World; this is a probe, and reading it
        // beats widening the client's surface for a measurement.
        var manager = (Game.LinkedObject)typeof(World)
            .GetField("_effectManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(world);

        int n = 0;

        for (var e = manager?.Items; e != null; e = e.Next)
        {
            n++;
        }

        return n;
    }
}
