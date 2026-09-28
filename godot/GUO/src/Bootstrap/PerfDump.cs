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
/// "[go X Y Z" (the character must be a GM). SECONDS later a timer reads the
/// profiler's averages over its last 60 frames (per drawn frame, whatever the
/// frame rate) and writes DIR\LABEL\&lt;client&gt;.json. No keystrokes, so the
/// window never needs the focus.
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
        _timer = new Timer(_ => Write(), null, seconds * 1000, Timeout.Infinite);
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
                ["world_prepare_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE), 3),
                ["world_draw_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_WORLD), 3),
                ["render_frame_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME), 3),
                ["render_ui_ms"] = Math.Round(Avg(Profiler.ProfilerContext.RENDER_FRAME_UI), 3),
                ["update_world_ms"] = Math.Round(Avg(Profiler.ProfilerContext.UPDATE_WORLD), 3),
                ["frames_averaged"] = Profiler.ProfileTimeCount,
            };
            string dir = Path.Combine(_args[0], _args[1]);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ClientName + ".json");
            // By hand: ClassicUO's build turns reflection-based System.Text.Json off.
            string Value(object v) => v is string t
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
