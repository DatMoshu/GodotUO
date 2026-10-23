// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Renderer;

namespace GUO.Host;

/// <summary>
/// Frame time in fixed scenes (Epic B, B1): the login screen, an open field,
/// the Britain bank, a dense forest, a dungeon, and a run through ground that
/// loads as it comes into view (review R1-1). Per scene it reports
/// the mean, p95 and p99 wall-clock frame time with vsync and the frame cap
/// off, draw calls, and the batcher's own counts -- canvas items opened (each
/// is a batch break), texture switches and draw commands -- next to the
/// client's profiler phases. Written as a markdown table and JSON into
/// <c>--perf-out</c> (launchers\dev\perf_probe.bat puts it in build\perf); a
/// relative one is under the project folder, godot/GUO.
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

    private sealed record Scene(string Name, string Go, string What, bool Walks = false);

    // Places also used by tools\ab_compare where they overlap, so a scene can be
    // photographed there and timed here.
    private static readonly Scene[] Scenes =
    {
        new("open-field", "[go 1164 1668", "Felucca grassland south of Britain: land and a few trees"),
        new("britain-bank", "[go 1434 1699 0", "the Britain bank: dense statics, roofs, vendors and passers-by"),
        new("dense-forest", "[go 633 858", "the forest east of Yew (ab_compare yew-forest)"),
        new("dungeon", "[go 5401 629", "the mouth of Despise (ab_compare despise-mouth): dungeon light level, torches"),
        // ADR-0037: the water budget's scenes, on a pier with water on every side.
        new("britain-docks", "[go 1499 1761 -3", "the Britain docks: water land and water statics around a pier"),
        new("trinsic-bay", "[go 2087 2861 -3", "the Trinsic bay: water at three heights around a pier"),
        // The community's cases (PERF-03): a crypt at z -60 under land at
        // z 0, where everything above the roof must be culled, and open sea
        // with no statics. Their dense city is britain-bank, their forest
        // dense-forest.
        new("cave-roof", "[go 1010 760 -60", "a crypt under the land near Yew: a floor at z -60 under land at z 0"),
        new("open-sea", "[go 3600 1400", "open sea between Vesper and Moonglow: water and nothing on it"),
        // Standing still, nothing new reaches the atlas; running, new land and
        // statics are added to its pages almost every step, and with
        // --merged-land=array or --merged-cover each changed page is copied
        // again into the land array (LandPages, 16 MiB a layer). Last, since it
        // leaves the player elsewhere.
        new("walk", "[go 1164 1668", "running from the open field, turning where blocked: ground that loads as it comes into view", Walks: true),
    };

    /// <summary>--perf-scene NAME, repeatable: these world scenes only (all when empty).</summary>
    public static readonly List<string> Only = new();

    /// <summary>
    /// --perf-expect-character NAME and --perf-expect-map N: the run measures
    /// nothing unless it is in the world as that character on that map, and
    /// never creates a character to get there. A scene whose [go did not
    /// land within three tiles of its target is not captured either.
    /// </summary>
    public static string ExpectCharacter { get; set; }
    public static int ExpectMap { get; set; } = -1;

    /// <summary>--perf-expect-account NAME: the allocated account; InputProbe.Strict logs in as it and nothing else.</summary>
    public static string ExpectAccount { get; set; }

    /// <c>--perf-static-world</c>: the tile walk leaves out every mobile but
    /// the player, every item and every effect, so a scene's counts depend on
    /// the map alone. The count gate (tools/perf_probe/gate.py) measures with
    /// it: the dungeon's batcher items moved between two identical runs (158
    /// to 160) and failed a stock gate (PERF-V6); the working hypothesis is
    /// dynamic content (a passer-by, a spawn and its loot at the mouth of
    /// Despise), not yet reproduced under control, and a map-only world is
    /// the proposed stabilisation. Frame times under this flag are lower
    /// than play; they are advisory there.
    ///
    /// Set by <see cref="Run"/> for the probe's lifetime and cleared when it
    /// ends, never by argument parsing: the flag is refused without
    /// <c>--perf-probe</c> (<see cref="PerfProbeFlags.ProbeOnlyError"/>), so
    /// ordinary play can never run with a thinned world.
    /// </summary>
    public static bool StaticWorld { get; private set; }

    private const int Settle = 180;
    private static string _outDir, _label;
    private static bool _parity;
    private static string _profilePreset = "", _profileSummary = "";

    /// <summary>
    /// The switch --perf-parity flips: the experimental path under test,
    /// --batched-world by default, --merged-land when that flag is on.
    /// </summary>
    public static System.Action<bool> ParityToggle = on => UltimaBatcher2D.BatchedWorld = on;
    public static System.Func<bool> ParityState = () => UltimaBatcher2D.BatchedWorld;
    private const int Frames = 360;
    private const double MinDrawsPerFrame = 0.95;

    /// <summary>
    /// A run is timed, not counted in frames: uncapped, a desktop draws a
    /// thousand frames and more a second, a handheld sixty, and the ground
    /// covered is what matters. Twelve seconds of running crosses several chunks.
    /// </summary>
    private const double WalkMs = 12000;

    /// <summary>
    /// Runs the player on, a step whenever the walker takes one, turning to the
    /// next heading when blocked for a second (by the clock: a frame can be well
    /// under a millisecond, and a running step takes a fifth of a second);
    /// counts the tiles covered.
    /// </summary>
    private sealed class Runner
    {
        private static readonly Game.Data.Direction[] Headings =
        {
            Game.Data.Direction.West, Game.Data.Direction.Left, Game.Data.Direction.Up,
            Game.Data.Direction.South, Game.Data.Direction.North, Game.Data.Direction.East,
        };

        private int _heading;
        private ulong _movedAt;
        private int _x = -1, _y = -1;

        public int Tiles { get; private set; }

        public void Step()
        {
            var player = Client.Game.UO.World?.Player;
            if (player == null)
            {
                return;
            }

            ulong now = Godot.Time.GetTicksMsec();
            if (_x < 0 || player.X != _x || player.Y != _y)
            {
                if (_x >= 0)
                {
                    Tiles += System.Math.Max(System.Math.Abs(player.X - _x), System.Math.Abs(player.Y - _y));
                }

                _movedAt = now;
            }
            else if (now - _movedAt > 1000)
            {
                _heading = (_heading + 1) % Headings.Length;
                _movedAt = now;
            }

            _x = player.X;
            _y = player.Y;
            player.Walk(Headings[_heading], true);
        }
    }

    public static async System.Threading.Tasks.Task Run(Node host, string outDir, string label, float zoom = 0, bool parity = false,
        string profilePreset = null, bool staticWorld = false)
    {
        StaticWorld = staticWorld;
        try
        {
            await RunProbe(host, outDir, label, zoom, parity, profilePreset);
        }
        finally
        {
            StaticWorld = false;
        }
    }

    private static async System.Threading.Tasks.Task RunProbe(Node host, string outDir, string label, float zoom, bool parity, string profilePreset)
    {
        _profilePreset = profilePreset ?? "";
        _profileSummary = "";
        _outDir = string.IsNullOrWhiteSpace(outDir) ? ProjectSettings.GlobalizePath("user://perf") : outDir;
        _label = label;
        _parity = parity;
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

        // The allocated account's credential has to exist, the configured
        // account has to be the allocated one and autologin has to be off
        // before anything connects (LoginPreflight.Decide, the decision Main
        // already took at startup); a run that fails it stops here with the
        // login row only.
        if (ExpectAccount != null
            && !InputProbe.StrictCredential(ExpectAccount, System.Environment.GetEnvironmentVariable, out _, out string noCredential))
        {
            GD.PrintErr($"[GUO] perf probe: not connecting: {noCredential}");
            GUO.Utility.Profiler.Enabled = profiling;
            Write(outDir, label, results);
            return;
        }

        // On a device, logged in as DualProbe does: the touch layer would
        // swallow the probe's clicks, which aim in client pixels.
        bool touch = GUO.Input.Touch.TouchInput.Enabled;
        GUO.Input.Touch.TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;
        await InputProbe.EnterTheWorld(host, 200);
        InputProbe.PointerScale = 1f;
        GUO.Input.Touch.TouchInput.Enabled = touch;
        World world = Client.Game.UO.World;
        if (!world.InGame)
        {
            GD.PrintErr("[GUO] perf probe: never got into the world.");
            GUO.Utility.Profiler.Enabled = profiling;
            Write(outDir, label, results);
            return;
        }

        // The preflight: the world entered as the allocated account (the one
        // the client connected with and the shard let in, as the loaded
        // profile records it; the shard's own record is not read) and the
        // expected character, on the expected map, or no scene is visited
        // and nothing is captured (the login row is kept).
        var profile = ProfileManager.CurrentProfile;
        string identity = $"account {profile?.Username}, character {profile?.CharacterName} (player {world.Player?.Name}),"
            + $" map {world.MapIndex} at {world.Player?.X},{world.Player?.Y},{world.Player?.Z}, connects {InputProbe.ConnectCalls}";
        if (!LoginPreflight.IdentityMatches(profile?.Username, profile?.CharacterName, ExpectAccount, ExpectCharacter, out string wrongIdentity)
            || (ExpectCharacter != null && !string.Equals(world.Player?.Name, ExpectCharacter, System.StringComparison.Ordinal)))
        {
            GD.PrintErr($"[GUO] perf probe: {wrongIdentity ?? "the player is not the expected character"}; in the world as {identity}; measuring nothing.");
            GUO.Utility.Profiler.Enabled = profiling;
            Write(outDir, label, results);
            return;
        }

        if (ExpectMap >= 0 && world.MapIndex != ExpectMap)
        {
            GD.PrintErr($"[GUO] perf probe: expected map {ExpectMap}, in the world as {identity}; measuring nothing.");
            GUO.Utility.Profiler.Enabled = profiling;
            Write(outDir, label, results);
            return;
        }

        GD.Print($"[GUO] perf probe: in the world as {identity}");

        // --perf-profile NAME: the loaded profile set to a preset (plain, as
        // ClassicUO draws; uoplus-max, everything on) before any scene is timed.
        _profileSummary = string.IsNullOrWhiteSpace(_profilePreset)
            ? PerfProfilePreset.Describe(GUO.Configuration.ProfileManager.CurrentProfile)
            : PerfProfilePreset.Apply(_profilePreset, world);

        // --perf-zoom Z: the camera at Z (clamped to its range) for every world
        // scene. Zoomed out is where the sorted world costs most (ADR-0007).
        if (zoom > 0)
        {
            var camera = Client.Game.Scene.Camera;
            camera.Zoom = System.Math.Clamp(zoom, camera.ZoomMin, camera.ZoomMax);
            GD.Print($"[GUO] perf probe: zoom {camera.Zoom:F1} (asked {zoom:F1}, range {camera.ZoomMin:F1}-{camera.ZoomMax:F1})");
        }

        Scene[] scenes = Only.Count == 0 ? Scenes : System.Array.FindAll(Scenes, s => Only.Contains(s.Name));
        // A misspelt --perf-scene measured nothing and still passed (review M6).
        List<string> unknown = Only.Where(n => !Scenes.Any(s => s.Name == n)).ToList();
        if (unknown.Count > 0)
        {
            GD.PrintErr($"[GUO] perf probe: no scene named {string.Join(", ", unknown)}; the scenes are {string.Join(", ", Scenes.Select(s => s.Name))}");
        }

        bool arrivedEverywhere = true;
        foreach (Scene scene in scenes)
        {
            await InputProbe.Say(host, scene.Go);
            // Chunks newly in view load and build their meshes first; that is a
            // one-off, not what a player standing still pays every frame.
            await InputProbe.Wait(host, Settle);
            if (!Arrived(world, scene, out string where))
            {
                GD.PrintErr($"[GUO] perf probe: {scene.Name}: {scene.Go} did not arrive ({where}); not captured.");
                arrivedEverywhere = false;
                continue;
            }

            results.Add(await Measure(host, viewport, scene.Name, scene.What, scene.Walks));

            // With --perf-parity, the same spot again with the path under test
            // off: the frame-time comparison from one run (one export on a device).
            if (_parity)
            {
                bool was = ParityState();
                ParityToggle(false);
                _parity = false;
                if (scene.Walks)
                {
                    await InputProbe.Say(host, scene.Go);
                }

                await InputProbe.Wait(host, Settle);
                results.Add(await Measure(host, viewport, scene.Name + "-off", scene.What + " (the path under test off)", scene.Walks));
                _parity = true;
                ParityToggle(was);
                await InputProbe.Wait(host, 30);
            }
        }

        GUO.Utility.Profiler.Enabled = profiling;
        Write(outDir, label, results);
        // Every measured frame must have drawn (UltimaBatcher2D.FramesBegun):
        // a frame the client skipped times nothing (review M6).
        List<string> idle = results.Where(r => (double)r["draws_per_frame"] < MinDrawsPerFrame).Select(r => $"{r["scene"]} ({r["draws_per_frame"]})").ToList();
        if (idle.Count > 0)
        {
            GD.PrintErr($"[GUO] perf probe: frames that drew nothing in {string.Join(", ", idle)}; fewer than {MinDrawsPerFrame} draws per frame");
        }

        // A parity row whose test captures did not all come from the pass is
        // not a result for it; the reviewer reads gpu_test_on_pass in the
        // report, and the run does not pass with such a row either.
        bool capturesValid = !GpuWorld.Enabled || results.Where(r => r.ContainsKey("gpu_test_on_pass"))
            .All(r => (int)r["gpu_test_on_pass"] == (int)r["gpu_test_captures"]);
        if (!capturesValid)
        {
            GD.PrintErr("[GUO] perf probe: a parity row has test captures the --gpu-world pass did not draw; see gpu_test_on_pass");
        }

        Passed = unknown.Count == 0 && idle.Count == 0 && arrivedEverywhere && capturesValid
            && results.Count == (_parity ? 2 : 1) * scenes.Length + 1;
    }

    /// <summary>The player stands at the scene's [go target: x, y, z and map, by LoginPreflight.ArrivedAt.</summary>
    private static bool Arrived(World world, Scene scene, out string where)
    {
        var player = world.Player;
        if (player == null)
        {
            where = "no player";
            return false;
        }

        return LoginPreflight.ArrivedAt(player.X, player.Y, player.Z, world.MapIndex, scene.Go, ExpectMap, out where);
    }

    private static async System.Threading.Tasks.Task<Dictionary<string, object>> Measure(Node host, Rid viewport, string name, string what,
        bool walks = false)
    {
        // A pixel comparison needs a still frame; a run has none.
        Dictionary<string, object> parity = _parity && !walks ? await Parity(host, name) : null;
        int n = Frames;
        Runner runner = walks ? new Runner() : null;

        // Uncapped: with vsync or a frame cap every frame below the cap reads as
        // the cap, and a renderer change that saves 4 ms would not show.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        int maxFps = Engine.MaxFps;
        Engine.MaxFps = 0;
        // Upstream's own pacing (GameController._intervalFixedUpdate) skips the
        // draw when a frame comes early: one draw per 1000/FPS ms. (Its 217 ms
        // inactive tick does not apply: GameController.IsActive counts a
        // scripted, unfocusable window as active.) Uncapped, the engine runs
        // far faster than that interval, so left alone most measured frames
        // would draw nothing. Made tiny for the measurement, so every frame
        // draws; put back after.
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

        var ms = new List<double>(walks ? 20000 : n);
        double kRect = 0, kAffine = 0, kMesh = 0, kTri = 0, kBatches = 0, kCover = 0, kCoverRuns = 0, coverQueued = 0, coverOverlapping = 0;
        double calls = 0, items = 0, switches = 0, commands = 0, prepare = 0, world = 0, cpu = 0, gpu = 0, objects = 0;
        // Allocate the probe's own storage before the measured interval.
        var uploadMs = new List<double>();
        bool uploadedLast = false;
        int stages = (int)PerfStages.Stage.Count;
        double[] stageMs = new double[stages], frameStageMs = new double[stages];
        long[] stageDrawn = new long[stages];
        int[] frameDrawn = new int[stages];
        double renderFrame = 0, ui = 0, update = 0;
        PerfStages.Enabled = true;
        // Align cumulative counters, stage reset and the wall clock to the
        // same ProcessFrame boundary. Previously counters also included this
        // unmeasured interval, and the first Take contained two intervals.
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        PerfStages.Take(frameStageMs, frameDrawn);
        long drawsBefore = UltimaBatcher2D.FramesBegun;
        // An upload lands in the frame after the one that drew it: its cost
        // is in the next frame's time. Keep upload frames and their successors
        // apart, since a p99 over thousands of frames can hide a few dozen.
        int uploadsBefore = LandPages.Uploads, uploadsMax = 0, uploadFrames = 0, uploadsLast = LandPages.Uploads;
        int rebuildsBefore = LandPages.Rebuilds, bumpsBefore = LandPages.Bumps;
        long terrainRebuildsBefore = CachedLand.TotalRebuilds;
        long terrainSpritesBefore = CachedLand.TotalRebuiltSprites;
#if GUO_LABS
        long rdBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.FramesDrawn;
        long rdFallbackBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.FramesFallback;
        long rdBytesBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.UploadedBytes;
        long rdCallsBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.DrawCalls;
        long rdTicksBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.RecordTicks;
        long rdSuppressedBefore = GUO.Labs.GpuRenderer.OrderedWorldCapture.SuppressedCommands;
        long rdReplayBefore = GUO.Labs.GpuRenderer.OrderedWorldCanvas.CommandsDrawn;
        long rdCanvasClearsBefore = UltimaBatcher2D.OrderedCanvasItemsCleared;
        long rdGeometryBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.GeometryBytes;
        long rdOrderBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.OrderBytes;
        long rdStateBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.StateBytes;
        long rdStateUploadsBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.StateUploadCalls;
        long rdStateChangesBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.ChangedStateStreams;
        long rdStateGrowthBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.StateBufferGrowths;
        long rdTextureRunsBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.TextureRuns;
        long rdOccurrencesBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.Occurrences;
        long rdUploadsBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.UploadCalls;
        long rdDirtyBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.DirtyQuads;
        long rdGrowthBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.BufferGrowths;
        long rdEvictionsBefore = GUO.Labs.GpuRenderer.OrderedWorldDevice.CacheEvictions;
#endif
        int landOntoBefore = GUO.Renderer.Arts.Art.LandOntoLandLayer, staticOntoBefore = GUO.Renderer.Arts.Art.StaticOntoLandLayer;
        long allocatedBefore = System.GC.GetTotalAllocatedBytes();
        System.TimeSpan pausedBefore = System.GC.GetTotalPauseDuration();
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = 0;
        for (int i = 0; walks ? elapsed < WalkMs : i < n; i++)
        {
            runner?.Step();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double frameMs = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            ms.Add(frameMs);
            elapsed += frameMs;
            last = now;
            int up = LandPages.Uploads - uploadsLast;
            uploadsLast = LandPages.Uploads;
            uploadsMax = System.Math.Max(uploadsMax, up);
            uploadFrames += up > 0 ? 1 : 0;
            if (up > 0 || uploadedLast)
            {
                uploadMs.Add(frameMs);
            }

            uploadedLast = up > 0;
            calls += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            objects += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
            (int sw, int it, int cm) = UltimaBatcher2D.LastFrame;
            switches += sw;
            items += it;
            commands += cm;
            var k = UltimaBatcher2D.LastKinds;
            kRect += k.Rects;
            kAffine += k.Affine;
            kMesh += k.Meshes;
            kTri += k.Triangles;
            kBatches += k.Batches;
            kCover += k.CoverMeshes;
            kCoverRuns += k.CoverRuns;
            var cq = GUO.Game.Scenes.RenderLists.LastCover;
            coverQueued += cq.Queued;
            coverOverlapping += cq.Overlapping;
            prepare += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE).LastTime;
            world += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_WORLD).LastTime;
            cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            PerfStages.Take(frameStageMs, frameDrawn);
            for (int s = 0; s < stages; s++)
            {
                stageMs[s] += frameStageMs[s];
                stageDrawn[s] += frameDrawn[s];
            }

            renderFrame += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME).LastTime;
            ui += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.RENDER_FRAME_UI).LastTime;
            update += GUO.Utility.Profiler.GetContext(GUO.Utility.Profiler.ProfilerContext.UPDATE_WORLD).LastTime;
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
        }

        PerfStages.Enabled = false;
        n = ms.Count;
        long draws = UltimaBatcher2D.FramesBegun - drawsBefore;
        int uploads = LandPages.Uploads - uploadsBefore;
        double allocatedKb = (System.GC.GetTotalAllocatedBytes() - allocatedBefore) / 1024.0 / n;
        double gcMs = (System.GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds / n;
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
            ["frames"] = n,
            ["draws_per_frame"] = System.Math.Round((double)draws / n, 2),
            ["mean_ms"] = System.Math.Round(ms.Average(), 3),
            ["p95_ms"] = System.Math.Round(P(0.95), 3),
            ["p99_ms"] = System.Math.Round(P(0.99), 3),
            ["fps"] = System.Math.Round(1000.0 / ms.Average(), 1),
            ["draw_calls"] = System.Math.Round(calls / n, 1),
            ["canvas_objects"] = System.Math.Round(objects / n, 1),
            ["batcher_items"] = System.Math.Round(items / n, 1),
            ["texture_switches"] = System.Math.Round(switches / n, 1),
            ["draw_commands"] = System.Math.Round(commands / n, 1),
            ["world_prepare_ms"] = System.Math.Round(prepare / n, 3),
            ["world_draw_ms"] = System.Math.Round(world / n, 3),
            ["render_cpu_ms"] = System.Math.Round(cpu / n, 3),
            ["render_gpu_ms"] = System.Math.Round(gpu / n, 3),
            ["alloc_kb_per_frame"] = System.Math.Round(allocatedKb, 1),
            ["terrain_dirty_chunks_per_frame"] = System.Math.Round((double)(CachedLand.TotalRebuilds - terrainRebuildsBefore) / n, 3),
            ["terrain_rebuilt_sprites_per_frame"] = System.Math.Round((double)(CachedLand.TotalRebuiltSprites - terrainSpritesBefore) / n, 1),
#if GUO_LABS
            ["rd_frames_drawn"] = GUO.Labs.GpuRenderer.OrderedWorldCapture.FramesDrawn - rdBefore,
            ["rd_frames_fallback"] = GUO.Labs.GpuRenderer.OrderedWorldCapture.FramesFallback - rdFallbackBefore,
            ["rd_upload_bytes_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldCapture.UploadedBytes - rdBytesBefore) / n, 1),
            ["rd_draw_calls_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldCapture.DrawCalls - rdCallsBefore) / n, 1),
            ["rd_record_cpu_ms"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldCapture.RecordTicks - rdTicksBefore) * 1000 / System.Diagnostics.Stopwatch.Frequency / n, 3),
            ["rd_last_fallback"] = GUO.Labs.GpuRenderer.OrderedWorldCapture.LastFallback ?? "",
            ["rd_deferred_quads_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldCapture.SuppressedCommands - rdSuppressedBefore) / n, 1),
            ["rd_canvas_replay_quads_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldCanvas.CommandsDrawn - rdReplayBefore) / n, 1),
            ["rd_canvas_items_cleared_per_frame"] = System.Math.Round((double)(UltimaBatcher2D.OrderedCanvasItemsCleared - rdCanvasClearsBefore) / n, 1),
            ["rd_geometry_bytes_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.GeometryBytes - rdGeometryBefore) / n, 1),
            ["rd_order_bytes_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.OrderBytes - rdOrderBefore) / n, 1),
            ["rd_state_bytes_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.StateBytes - rdStateBefore) / n, 1),
            ["rd_state_upload_calls_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.StateUploadCalls - rdStateUploadsBefore) / n, 3),
            ["rd_changed_state_streams_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.ChangedStateStreams - rdStateChangesBefore) / n, 3),
            ["rd_state_buffer_growths"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.StateBufferGrowths - rdStateGrowthBefore,
            ["rd_texture_runs_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.TextureRuns - rdTextureRunsBefore) / n, 1),
            ["rd_occurrences_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.Occurrences - rdOccurrencesBefore) / n, 1),
            ["rd_active_state_bytes"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.ActiveStateBytes,
            ["rd_state_capacity_bytes"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.StateCapacityBytes,
            ["rd_upload_calls_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.UploadCalls - rdUploadsBefore) / n, 3),
            ["rd_dirty_quads_per_frame"] = System.Math.Round((double)(GUO.Labs.GpuRenderer.OrderedWorldDevice.DirtyQuads - rdDirtyBefore) / n, 1),
            ["rd_cached_quads"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.CachedQuads,
            ["rd_peak_cached_quads"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.PeakCachedQuads,
            ["rd_buffer_growths"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.BufferGrowths - rdGrowthBefore,
            ["rd_cache_evictions"] = GUO.Labs.GpuRenderer.OrderedWorldDevice.CacheEvictions - rdEvictionsBefore,
#endif
            ["cmd_rects"] = System.Math.Round(kRect / n, 1),
            ["cmd_affine_rects"] = System.Math.Round(kAffine / n, 1),
            ["cmd_meshes"] = System.Math.Round(kMesh / n, 1),
            ["cmd_cover_meshes"] = System.Math.Round(kCover / n, 1),
            ["cover_runs"] = System.Math.Round(kCoverRuns / n, 1),
            ["cover_queued"] = System.Math.Round(coverQueued / n, 1),
            ["cover_overlapping"] = System.Math.Round(coverOverlapping / n, 1),
            ["cmd_triangles"] = System.Math.Round(kTri / n, 1),
            ["estimated_batches"] = System.Math.Round(kBatches / n, 1),
            ["gc_ms_per_frame"] = System.Math.Round(gcMs, 3),
            ["tiles_moved"] = runner?.Tiles ?? 0,
            ["land_layers"] = LandPages.Layers,
            ["land_capacity"] = LandPages.Capacity,
            ["land_array_mib"] = System.Math.Round((double)LandPages.Capacity * LandPages.LayerBytes / (1 << 20), 1),
            ["land_spare_mib"] = System.Math.Round((double)(LandPages.Capacity - LandPages.Layers) * LandPages.LayerBytes / (1 << 20), 1),
            ["video_mem_mib"] = System.Math.Round(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1 << 20), 1),
            ["texture_mem_mib"] = System.Math.Round(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / (1 << 20), 1),
            ["land_uploads"] = uploads,
            ["land_uploads_per_frame"] = System.Math.Round((double)uploads / n, 3),
            ["land_uploads_max_in_a_frame"] = uploadsMax,
            ["land_rebuild_copies"] = LandPages.Rebuilds - rebuildsBefore,
            ["land_bump_copies"] = LandPages.Bumps - bumpsBefore,
            ["land_sprites_onto_land_layers"] = GUO.Renderer.Arts.Art.LandOntoLandLayer - landOntoBefore,
            ["static_sprites_onto_land_layers"] = GUO.Renderer.Arts.Art.StaticOntoLandLayer - staticOntoBefore,
            ["frames_with_land_upload"] = uploadFrames,
            ["land_upload_mib_per_frame"] = System.Math.Round((double)uploads * LandPages.LayerBytes / (1 << 20) / n, 2),
            ["upload_frames_mean_ms"] = uploadMs.Count > 0 ? System.Math.Round(uploadMs.Average(), 3) : 0.0,
            ["upload_frames_max_ms"] = uploadMs.Count > 0 ? System.Math.Round(uploadMs.Max(), 3) : 0.0,
            ["max_ms"] = System.Math.Round(ms.Max(), 3),
            ["render_frame_ms"] = System.Math.Round(renderFrame / n, 3),
            ["ui_ms"] = System.Math.Round(ui / n, 3),
            ["update_world_ms"] = System.Math.Round(update / n, 3),
        };
        for (int s = 0; s < stages; s++)
        {
            string stage = StageName((PerfStages.Stage)s);
            r[$"stage_{stage}_ms"] = System.Math.Round(stageMs[s] / n, 3);
            if (stageDrawn[s] > 0)
            {
                r[$"drawn_{stage}"] = System.Math.Round((double)stageDrawn[s] / n, 1);
            }
        }
        if (parity != null)
        {
            foreach (var kv in parity)
            {
                r[kv.Key] = kv.Value;
            }
        }
        GD.Print($"[GUO] perf probe: {name}: mean {r["mean_ms"]} ms, p95 {r["p95_ms"]}, p99 {r["p99_ms"]}, "
                 + $"{r["draw_calls"]} draw calls, {r["batcher_items"]} batcher items, {r["texture_switches"]} texture switches, "
                 + $"{r["draw_commands"]} commands"
                 + (walks || uploads > 0 ? $"; {r["tiles_moved"]} tiles moved, {uploads} land-array uploads ({r["land_upload_mib_per_frame"]} MiB/frame)" : ""));
        return r;
    }

    /// <summary>
    /// Pixel parity of --batched-world against the plain path in this scene:
    /// five frames in order, plain, test, plain, test, plain. A pixel the three
    /// plain frames agree on is stable; both test frames agreeing on another
    /// value is a repeatable violation. Save every input for independent review.
    /// </summary>
    private static async System.Threading.Tasks.Task<Dictionary<string, object>> Parity(Node host, string name)
    {
        bool was = ParityState();
        // PORT DEVIATION (GUO): --gpu-world. Each test capture is taken from
        // the frame just drawn; whether that frame went through the pass is
        // read then (GpuWorld.LastFrameOnPass), not inferred from counters.
        int testCaptures = 0, testOnPass = 0;
#if GUO_LABS
        int rdTestOnPass = 0;
#endif
        async System.Threading.Tasks.Task<Image> Frame(bool batched)
        {
            ParityToggle(batched);
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (batched)
            {
                testCaptures++;
#if GUO_LABS
                if (GUO.Labs.GpuRenderer.OrderedWorldCapture.LastFrameOnPass) rdTestOnPass++;
#endif
                if (GpuWorld.LastFrameOnPass)
                {
                    testOnPass++;
                }
            }

            return host.GetViewport().GetTexture().GetImage();
        }

        // Five frames: plain, test, plain, test, plain. A pixel the three plain
        // frames agree on is stable. "violations" (the first check) counts stable
        // pixels where the first test frame differs; "strict" counts those where
        // both test frames agree with each other and differ from plain -- a
        // repeatable rendering difference, which flicker or motion almost never is.
        // PORT DEVIATION (GUO): --gpu-world's frame counters over the test
        // frames, so a scene that fell back to the canvas is told apart from
        // one the pass drew (GpuWorld.FellBack logs the reason once).
        long gpuDrawnBefore = GpuWorld.FramesDrawn, gpuFallbackBefore = GpuWorld.FramesFallback;
        long gpuSuspendedBefore = GpuWorld.FramesSuspended;
        int coverSkippedBefore = UltimaBatcher2D.CoverSkipped;
        using Image a = await Frame(false), b = await Frame(true), c = await Frame(false), b2 = await Frame(true), c2 = await Frame(false);
        ParityToggle(was);
        long gpuDrawn = GpuWorld.FramesDrawn - gpuDrawnBefore, gpuFallback = GpuWorld.FramesFallback - gpuFallbackBefore;
        // GPU-03: covering tiles the pass left to the bake (no land-array
        // page) and suspensions over the captures, for the parity reviewer.
        long gpuSuspended = GpuWorld.FramesSuspended - gpuSuspendedBefore;
        int coverSkipped = UltimaBatcher2D.CoverSkipped - coverSkippedBefore;
        byte[] da = a.GetData(), db = b.GetData(), dc = c.GetData(), db2 = b2.GetData(), dc2 = c2.GetData();
        int bpp = da.Length / (a.GetWidth() * a.GetHeight());
        long stable = 0, violations = 0, strict = 0;
        // The largest channel difference of a repeatable pixel: 1 is rounding
        // (fp16 math, ADR-0023's identity pass); tens is a different texel.
        int strictMaxDelta = 0;
        using var mask = Image.CreateEmpty(a.GetWidth(), a.GetHeight(), false, Image.Format.Rgba8);
        bool Eq(byte[] x, byte[] y, int at)
        {
            for (int k = 0; k < bpp; k++)
            {
                if (x[at + k] != y[at + k])
                {
                    return false;
                }
            }

            return true;
        }

        for (int i = 0, px = 0; i + bpp <= da.Length; i += bpp, px++)
        {
            if (!Eq(da, dc, i) || !Eq(da, dc2, i))
            {
                continue;
            }

            stable++;
            if (Eq(da, db, i))
            {
                continue;
            }

            violations++;
            bool repeatable = Eq(db, db2, i);
            if (repeatable)
            {
                strict++;
                for (int k = 0; k < bpp; k++)
                {
                    strictMaxDelta = System.Math.Max(strictMaxDelta, System.Math.Abs(da[i + k] - db[i + k]));
                }
            }

            mask.SetPixel(px % a.GetWidth(), px / a.GetWidth(), repeatable ? Colors.Red : Colors.Yellow);
        }

        Directory.CreateDirectory(_outDir);
        string stem = Path.Combine(_outDir, $"parity_{_label}_{name}");
        void Save(Image image, string suffix)
        {
            string path = stem + suffix;
            Error error = image.SavePng(path);
            if (error != Error.Ok)
            {
                throw new IOException($"Could not save parity evidence {path}: {error}");
            }
        }

        Save(a, "_plain.png");
        Save(b, "_batched.png");
        Save(c, "_plain2.png");
        Save(b2, "_batched2.png");
        Save(c2, "_plain3.png");
        if (violations > 0)
        {
            Save(mask, "_violations.png");
        }

        GD.Print($"[GUO] perf probe: parity {name}: {violations} of {stable} stable pixels differ, {strict} repeatably (max channel delta {strictMaxDelta})"
            + (GpuWorld.Enabled ? $"; gpu-world: {testOnPass} of {testCaptures} test captures on the pass; over the five captures {gpuDrawn} frames drawn, {gpuFallback} fallback{(gpuFallback > 0 ? " (" + GpuWorld.LastFallback + ")" : "")}, {gpuSuspended} suspended, {coverSkipped} cover tiles skipped" : ""));
        return new Dictionary<string, object>
        {
            ["parity_stable_px"] = stable, ["parity_violations"] = violations, ["parity_strict"] = strict,
            ["parity_frames"] = new[] { "_plain.png", "_batched.png", "_plain2.png", "_batched2.png", "_plain3.png" }
                .Select(suffix => Path.GetFileName(stem + suffix)).ToArray(),
#if GUO_LABS
            ["rd_test_on_pass"] = rdTestOnPass,
#endif
            ["parity_strict_max_delta"] = strictMaxDelta,
            ["gpu_test_captures"] = testCaptures, ["gpu_test_on_pass"] = testOnPass,
            ["gpu_frames_drawn"] = gpuDrawn, ["gpu_frames_fallback"] = gpuFallback,
            ["gpu_last_fallback"] = gpuFallback > 0 ? GpuWorld.LastFallback ?? "" : "",
            ["gpu_suspended"] = gpuSuspended, ["gpu_cover_skipped"] = coverSkipped,
        };
    }

    /// <summary>A stage's name in the report: snake case of the enum name.</summary>
    private static string StageName(PerfStages.Stage stage) =>
        string.Concat(stage.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static void Write(string outDir, string label, List<Dictionary<string, object>> results)
    {
        string dir = string.IsNullOrWhiteSpace(outDir) ? ProjectSettings.GlobalizePath("user://perf") : outDir;
        Directory.CreateDirectory(dir);
        string stem = string.IsNullOrWhiteSpace(label) ? "perf" : $"perf_{label}";
        Vector2I size = DisplayServer.WindowGetSize();
        // Debug builds (the editor's) have the JIT optimiser off; every
        // absolute number depends on it, so it is written down.
        bool optimized = !(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>(typeof(PerfProbe).Assembly)?.IsJITOptimizerDisabled ?? false);
        var godotApi = typeof(GodotObject).Assembly;
        bool apiOptimized = !(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>(godotApi)?.IsJITOptimizerDisabled ?? false);
        // The post-processing look timed with the world: a saved look adds
        // full-screen passes to every number (Ink Outline on the Thor, 2026-09-28).
        var postFx = GUO.Renderer.PostFx.PostFxStack.Instance.Preset;
        string look = $"{postFx.Name} ({postFx.Passes.Count(p => p.Enabled)} pass(es)"
                      + (GUO.Renderer.PostFx.PostFxStack.RunOverride != null ? ", --postfx" : ", saved look") + ")";
        var md = new StringBuilder();
        md.AppendLine($"# Frame time: {label}");
        md.AppendLine();
        md.AppendLine($"{System.DateTime.Now:yyyy-MM-dd HH:mm}, {OS.GetName()}, {RenderingServer.GetVideoAdapterName()}, "
                      + $"window {size.X}x{size.Y}, zoom {Client.Game?.Scene?.Camera?.Zoom:F1}, vsync and frame cap off, "
                      + $"{Frames} frames per scene ({WalkMs / 1000:0} s for a run) after {Settle} to settle, "
                      + (optimized ? "optimised build" : "UNOPTIMISED build (Debug, Optimize=false)") + $", post-processing {look}, "
                      + $"profile {(string.IsNullOrWhiteSpace(_profilePreset) ? "as saved" : "preset " + _profilePreset)}: {_profileSummary}.");
        md.AppendLine();
        md.AppendLine($"Loaded Godot managed API JIT optimized: {apiOptimized}; engine debug build: {OS.IsDebugBuild()}.");
        md.AppendLine("| Scene | mean ms | p95 ms | p99 ms | FPS | alloc KB/frame | draws/frame | draw calls | batcher items | texture switches | draw commands | world prepare ms | world draw ms | render CPU ms | GPU ms |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            md.AppendLine($"| {r["scene"]} | {r["mean_ms"]} | {r["p95_ms"]} | {r["p99_ms"]} | {r["fps"]} | {r["alloc_kb_per_frame"]} | {r["draws_per_frame"]} | {r["draw_calls"]} | "
                          + $"{r["batcher_items"]} | {r["texture_switches"]} | {r["draw_commands"]} | {r["world_prepare_ms"]} | "
                          + $"{r["world_draw_ms"]} | {r["render_cpu_ms"]} | {r["render_gpu_ms"]} |");
        }

        md.AppendLine();
        md.AppendLine("| Scene | draw calls | estimated batches | rects | transformed rects | chunk land meshes | covering land meshes | covering runs | covering queued | of which overlap | triangle lists |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            md.AppendLine($"| {r["scene"]} | {r["draw_calls"]} | {r["estimated_batches"]} | {r["cmd_rects"]} | {r["cmd_affine_rects"]} | {r["cmd_meshes"]} | {r["cmd_cover_meshes"]} | {r["cover_runs"]} | {r["cover_queued"]} | {r["cover_overlapping"]} | {r["cmd_triangles"]} |");
        }

        md.AppendLine();
        md.AppendLine("| Scene | tiles moved | land layers | land-array uploads | per frame | most in a frame | frames with one | MiB/frame | those frames and the next: mean ms | their max ms | max ms of any frame |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            md.AppendLine($"| {r["scene"]} | {r.GetValueOrDefault("tiles_moved", 0)} | {r.GetValueOrDefault("land_layers", 0)} | {r.GetValueOrDefault("land_uploads", 0)} | "
                          + $"{r.GetValueOrDefault("land_uploads_per_frame", 0)} | {r.GetValueOrDefault("land_uploads_max_in_a_frame", 0)} | "
                          + $"{r.GetValueOrDefault("frames_with_land_upload", 0)} | {r.GetValueOrDefault("land_upload_mib_per_frame", 0)} | "
                          + $"{r.GetValueOrDefault("upload_frames_mean_ms", 0)} | {r.GetValueOrDefault("upload_frames_max_ms", 0)} | {r.GetValueOrDefault("max_ms", 0)} |");
        }

        // Where the world frame goes (PERF-03, src/Render/PerfStages.cs).
        string[] names = System.Enum.GetValues<PerfStages.Stage>().Where(s => s != PerfStages.Stage.Count).Select(StageName).ToArray();
        md.AppendLine();
        md.AppendLine("| Scene | update ms | render frame ms | UI ms | " + string.Join(" | ", names.Select(s => s.Replace('_', ' ') + " ms")) + " | statics drawn | mobiles drawn | items drawn | cover land drawn | multis drawn | other drawn |");
        md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", 3 + names.Length + 6)));
        foreach (var r in results)
        {
            md.AppendLine($"| {r["scene"]} | {r.GetValueOrDefault("update_world_ms", 0)} | {r.GetValueOrDefault("render_frame_ms", 0)} | {r.GetValueOrDefault("ui_ms", 0)} | "
                          + string.Join(" | ", names.Select(s => r.GetValueOrDefault($"stage_{s}_ms", 0)))
                          + $" | {r.GetValueOrDefault("drawn_statics", 0)} | {r.GetValueOrDefault("drawn_mobiles", 0)} | {r.GetValueOrDefault("drawn_items", 0)} | {r.GetValueOrDefault("drawn_cover_land", 0)} | {r.GetValueOrDefault("drawn_multis", 0)} | {r.GetValueOrDefault("drawn_other", 0)} |");
        }

        if (results.Any(r => r.ContainsKey("parity_violations")))
        {
            md.AppendLine();
            md.AppendLine("| Scene | parity: stable pixels | test differs | repeatably (strict) | strict max delta | test captures on the pass | frames drawn / fallback over the captures | last fallback | suspended | cover tiles skipped |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---:|---:|");
            foreach (var r in results.Where(r => r.ContainsKey("parity_violations")))
            {
                md.AppendLine($"| {r["scene"]} | {r["parity_stable_px"]} | {r["parity_violations"]} | {r["parity_strict"]} | {r.GetValueOrDefault("parity_strict_max_delta", "")}"
                    + $" | {r.GetValueOrDefault("gpu_test_on_pass", "")} of {r.GetValueOrDefault("gpu_test_captures", "")} | {r.GetValueOrDefault("gpu_frames_drawn", "")} / {r.GetValueOrDefault("gpu_frames_fallback", "")} | {r.GetValueOrDefault("gpu_last_fallback", "")}"
                    + $" | {r.GetValueOrDefault("gpu_suspended", "")} | {r.GetValueOrDefault("gpu_cover_skipped", "")} |");
            }
        }

        md.AppendLine();
        md.AppendLine("Batcher items: canvas items the batcher opened (each is a batch break). Draw commands: sprites, meshes and triangle lists added.");
        File.WriteAllText(Path.Combine(dir, stem + ".md"), md.ToString());
        File.WriteAllText(Path.Combine(dir, stem + ".json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["label"] = label, ["window"] = $"{size.X}x{size.Y}", ["zoom"] = System.Math.Round(Client.Game?.Scene?.Camera?.Zoom ?? 0, 2),
                    ["sample_window_version"] = 2,
                    ["optimized"] = optimized, ["postfx"] = look, ["profile_preset"] = _profilePreset, ["profile"] = _profileSummary, ["static_world"] = StaticWorld,
                    ["godot_api_jit_optimized"] = apiOptimized, ["godot_api_mvid"] = godotApi.ManifestModule.ModuleVersionId.ToString(),
                    ["engine_debug_build"] = OS.IsDebugBuild(),
                    ["system"] = SystemInfo(), ["scenes"] = results,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[GUO] perf probe: wrote {Path.Combine(dir, stem + ".md")}");
    }

    /// <summary>
    /// What the numbers were measured on, for a report someone else reads
    /// (tools\perf_lab): the GPU, its driver, the CPU and the OS. No paths and
    /// no names: nothing here identifies the person or the machine.
    /// </summary>
    private static Dictionary<string, object> SystemInfo()
    {
        string[] driver = OS.GetVideoAdapterDriverInfo();
        var engine = Engine.GetVersionInfo();
        return new Dictionary<string, object>
        {
            ["gpu"] = RenderingServer.GetVideoAdapterName(),
            ["gpu_vendor"] = RenderingServer.GetVideoAdapterVendor(),
            ["gpu_api"] = RenderingServer.GetVideoAdapterApiVersion(),
            ["driver"] = driver.Length > 0 ? driver[0] : "",
            ["driver_version"] = driver.Length > 1 ? driver[1] : "",
            ["rendering_method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["cpu"] = OS.GetProcessorName().Trim(),
            ["cpu_threads"] = OS.GetProcessorCount(),
            ["ram_gib"] = System.Math.Round((double)OS.GetMemoryInfo()["physical"] / (1L << 30), 1),
            ["os"] = OS.GetName(),
            ["os_version"] = OS.GetVersion(),
            ["screen"] = $"{DisplayServer.ScreenGetSize().X}x{DisplayServer.ScreenGetSize().Y}",
            ["refresh_hz"] = System.Math.Round(DisplayServer.ScreenGetRefreshRate(), 1),
            ["engine"] = $"{engine["major"]}.{engine["minor"]}.{engine["patch"]}.{engine["status"]}",
        };
    }
}
