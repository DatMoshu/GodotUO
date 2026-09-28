// SPDX-License-Identifier: BSD-2-Clause

// Compiled into GUO, and into the out-of-tree ClassicUO build (with
// PERF_DUMP_CUO defined, through tools\render_dump\inject.targets), so both
// clients time the same scene the same way with their own profiler. The two
// trees differ only in their root namespace, which is all the #if below
// swaps. sources\ is never edited.

#if PERF_DUMP_CUO
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Utility;
using ClientRoot = ClassicUO.Client;
#else
using GUO.Game;
using GUO.Game.Scenes;
using GUO.Utility;
using ClientRoot = GUO.Client;
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Linq;
using System.Threading;

#if PERF_DUMP_CUO
namespace ClassicUO.PerfDumpInjected;
#else
namespace GUO.Host;
#endif

/// <summary>
/// Epic B, B4: world prepare and world draw in one scene, from the client's
/// own profiler, so GUO can be held against ClassicUO in the same place.
/// </summary>
/// <remarks>
/// Off unless GUO_PERF_DUMP set to "DIR;LABEL;X;Y;Z;ZOOM;SECONDS". Once the
/// client is in the world, an action queued on the game loop (the main
/// thread, between frames) turns the profiler on, sets the camera zoom and says
/// "[go X Y Z" (the character must be a GM). SECONDS later a second queued action reads the
/// profiler's averages over its last 60 frames (per drawn frame, whatever the
/// frame rate) and writes DIR\LABEL\&lt;client&gt;.json. No keystrokes, so the
/// window never needs the focus. The result also says whether this build is
/// JIT-optimised, and how many objects the last frame's render lists held
/// (every list field of GameScene's RenderLists but the gump ones; the two
/// clients split them differently, so each is also written by name), read on the game
/// loop between frames, so prepare's cost can be compared per object. GUO also
/// writes how many land-array layers (LandPages, --merged-land=array or
/// --merged-cover) were copied between the fade ending and the write, and per
/// drawn frame (review R1-1); ClassicUO has no land array.
/// </remarks>
internal static class PerfDump
{
#if PERF_DUMP_CUO
    private const string ClientName = "cuo";
#else
    private const string ClientName = "guo";
#endif

    private static string[] _args;
    private static Timer _timer;
    private static object _attached;
    private static int _state;

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [ModuleInitializer]
    internal static void Start()
    {
        string spec = Environment.GetEnvironmentVariable("GUO_PERF_DUMP");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        _args = spec.Split(';');
        if (_args.Length < 7)
        {
            Console.WriteLine("[perf_dump] GUO_PERF_DUMP wants DIR;LABEL;X;Y;Z;ZOOM;SECONDS");
            return;
        }

        _timer = new Timer(_ => Attach(), null, 1000, 1000);
    }

    private static void Attach()
    {
        try
        {
            if (ClientRoot.Game?.Scene is not GameScene scene || Field(scene, "_world") is not World world
                || ReferenceEquals(world, _attached) || world.Player == null)
            {
                return;
            }

            // Onto the main thread through the game loop's own queue, a few
            // seconds on so the world has settled. Waiting for the first
            // message instead never fires on a quiet shard: the login's
            // messages come before this timer sees the player. The queue is a
            // plain list, so this one Add races the loop's walk of it; once,
            // in a harness that is off unless GUO_PERF_DUMP is set.
            _attached = world;
            ClientRoot.Game.EnqueueAction(3000, OnMainThread);
            Console.WriteLine($"[perf_dump] {ClientName}: in the world; starting in 3 s");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[perf_dump] attach failed: {e.Message}");
        }
    }

    private static void OnMainThread()
    {
        if (_state != 0)
        {
            return;
        }

        _state = 1;
        Profiler.Enabled = true;
        float zoom = float.Parse(_args[5], System.Globalization.CultureInfo.InvariantCulture);
        var camera = ClientRoot.Game.Scene.Camera;
        camera.Zoom = Math.Clamp(zoom, camera.ZoomMin, camera.ZoomMax);
        GameActions.Say($"[go {_args[2]} {_args[3]} {_args[4]}");
        Console.WriteLine($"[perf_dump] {ClientName}: [go {_args[2]} {_args[3]} {_args[4]}, zoom {camera.Zoom:F1}");
        int seconds = int.Parse(_args[6]);
        _timer.Dispose();
        _timer = null;

        // On the game loop, between frames: the render lists hold the last
        // frame's objects then (prepare clears them at its start).
        ClientRoot.Game.EnqueueAction((uint)(seconds * 1000), WaitForFade);
    }

    // After the [go everything in view fades in (AlphaHue below 255), and a
    // fading object is sorted and drawn one by one instead of from the chunk
    // mesh, so an average taken mid-fade measured the fade: ClassicUO read
    // 2-4x slower with 2,231 of 2,244 listed objects still fading. So wait
    // until the render lists' transparent list is down to a few, then let the
    // profiler's 60-frame window fill with frames after it. Capped, and both
    // what was left fading and how long it took are written down.
    private const int FadeLeft = 20;
    private const long FadeCapMs = 60_000;
    private static long _fadeStart = -1, _fadeWaitMs;
    private static int _fadingAtEnd = -1;
#if !PERF_DUMP_CUO
    private static int _uploadsAtFade, _bumpsAtFade, _staticOntoAtFade;
    private static long _framesAtFade;
#endif

    private static int Fading() =>
        Field(ClientRoot.Game.Scene, "_renderLists") is object lists
        && Field(lists, "_transparentObjects") is System.Collections.ICollection c ? c.Count : -1;

    private static void WaitForFade()
    {
        long now = Environment.TickCount64;
        if (_fadeStart < 0)
        {
            _fadeStart = now;
        }

        int fading = Fading();
        if (fading <= FadeLeft || now - _fadeStart >= FadeCapMs)
        {
            _fadingAtEnd = fading;
            _fadeWaitMs = now - _fadeStart;
#if !PERF_DUMP_CUO
            _uploadsAtFade = GUO.Renderer.LandPages.Uploads;
            _bumpsAtFade = GUO.Renderer.LandPages.Bumps;
            _staticOntoAtFade = GUO.Renderer.Arts.Art.StaticOntoLandLayer;
            _framesAtFade = GUO.Renderer.UltimaBatcher2D.FramesBegun;
#endif
            Console.WriteLine($"[perf_dump] {ClientName}: {fading} fading after {_fadeWaitMs} ms more; averaging");
            ClientRoot.Game.EnqueueAction(2000, Write);
            return;
        }

        ClientRoot.Game.EnqueueAction(500, WaitForFade);
    }

    private static void Write()
    {
        try
        {
            double Avg(Profiler.ProfilerContext c) => Profiler.GetContext(c).AverageTime;
            var world = _attached as World;
            var bounds = ClientRoot.Game.Window.ClientBounds;
            var r = new Dictionary<string, object>
            {
                ["client"] = ClientName,
                ["label"] = _args[1],
                ["at"] = world?.Player != null ? $"{world.Player.X},{world.Player.Y},{world.Player.Z}" : "",
                ["zoom"] = ClientRoot.Game.Scene.Camera.Zoom,
                ["window"] = $"{bounds.Width}x{bounds.Height}",
                // The world viewport in the client's own pixels. ClientBounds
                // can be in points under high DPI, so this, not "window", is
                // what says the two clients frame the same view.
                ["camera_bounds"] = $"{ClientRoot.Game.Scene.Camera.Bounds.Width}x{ClientRoot.Game.Scene.Camera.Bounds.Height}",
                ["world_prepare_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE), 3),
                ["world_draw_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_WORLD), 3),
                ["render_frame_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME), 3),
                ["render_ui_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_UI), 3),
                ["update_world_ms"] = Math.Round(Avg(Profiler.ProfilerContext.UPDATE_WORLD), 3),
                ["frames_averaged"] = Profiler.ProfileTimeCount,
                ["optimized"] = !(typeof(PerfDump).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false),
                ["rendered_objects"] = ClientRoot.Game.Scene.RenderedObjectsCount,
            };
            int listed = 0;
            if (Field(ClientRoot.Game.Scene, "_renderLists") is object lists)
            {
                foreach (FieldInfo f in lists.GetType().GetFields(Any))
                {
                    if (f.GetValue(lists) is System.Collections.ICollection c && f.FieldType.IsGenericType
                        && !f.Name.Contains("gump", StringComparison.OrdinalIgnoreCase))
                    {
                        r["list" + f.Name] = c.Count;
                        listed += c.Count;
                    }
                }
            }

            r["render_list_objects"] = listed;
            r["fading_when_averaging"] = _fadingAtEnd;
            r["fade_wait_ms"] = _fadeWaitMs;
            r["fade_timed_out"] = _fadeWaitMs >= FadeCapMs;
#if !PERF_DUMP_CUO
            int uploads = GUO.Renderer.LandPages.Uploads - _uploadsAtFade;
            long frames = GUO.Renderer.UltimaBatcher2D.FramesBegun - _framesAtFade;
            r["land_layers"] = GUO.Renderer.LandPages.Layers;
            r["land_uploads"] = uploads;
            r["land_uploads_per_frame"] = frames > 0 ? Math.Round((double)uploads / frames, 3) : 0.0;
            // Land drawn a second time over what is sunk under it (ADR-0004's
            // covering land) counts as rendered here and has no ClassicUO twin.
            r["cover_queued"] = GUO.Game.Scenes.RenderLists.LastCover.Queued;
            r["land_bump_copies"] = GUO.Renderer.LandPages.Bumps - _bumpsAtFade;
            r["static_sprites_onto_land_layers"] = GUO.Renderer.Arts.Art.StaticOntoLandLayer - _staticOntoAtFade;
#endif
            string dir = Path.Combine(_args[0], _args[1]);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ClientName + ".json");
            // By hand: ClassicUO's build turns reflection-based System.Text.Json off.
            string Value(object v) => v is bool yes ? (yes ? "true" : "false") : v is string t
                ? "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : Convert.ToString(v, CultureInfo.InvariantCulture);
            File.WriteAllText(path, "{\n" + string.Join(",\n", r.Select(kv => $"  \"{kv.Key}\": {Value(kv.Value)}")) + "\n}\n");
            Console.WriteLine($"[perf_dump] {ClientName}: wrote {path}");
            _state = 2;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[perf_dump] write failed: {e.Message}");
        }
    }

    private static object Field(object target, string name)
    {
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null)
            {
                return f.GetValue(target);
            }
        }

        return null;
    }
}
